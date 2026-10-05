import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { IMPORT_POLL_MS } from './import-detail';

const WS = '/api/v1/workspaces/ws-1';
const HEADER = ['BEGDOC', 'CUSTODIAN', 'NOTES'];

const delimiter = (char: string) => ({ char, codepoint: 'U+0000', decimal: char.charCodeAt(0) });

/** A preview where BEGDOC → Control Number (alias), CUSTODIAN → Custodian and NOTES follows the request. */
function preview(request: { profile?: { columns?: { column: string; ignore?: boolean }[] } }) {
  const notes = request.profile?.columns?.find((c) => c.column === 'NOTES');
  const mapped = (label: string, matchedBy: string, alias: string | null, fieldId: number) => ({
    target: { kind: 'field', fieldId, fieldName: label },
    label,
    type: 'keyword',
    isMultiValue: false,
    resolution: 'resolved',
    matchedBy,
    alias,
  });
  const columns = [
    {
      column: 'BEGDOC',
      status: 'mapped',
      targets: [mapped('Control Number', 'alias', 'BEGDOC', 1)],
    },
    {
      column: 'CUSTODIAN',
      status: 'mapped',
      targets: [mapped('Custodian', 'exactName', null, 10)],
    },
    { column: 'NOTES', status: notes?.ignore ? 'ignored' : 'unmapped', targets: [] },
  ].map((c, index) => ({
    ...c,
    index,
    mergedInto: null,
    sampleValues: [`${c.column.toLowerCase()}-1`],
    valueCount: 20,
    blankCount: 0,
    errorCount: c.column === 'CUSTODIAN' ? 2 : 0,
    warningCount: 0,
  }));
  return {
    canImport: true,
    columns,
    effectiveProfile: {
      columns: [
        { column: 'BEGDOC', ignore: false, targets: [{ kind: 'field', fieldId: 1 }] },
        ...(notes ? [notes] : []),
      ],
    },
    file: {
      encoding: 'utf-8',
      encodingSource: 'heuristic',
      delimiters: 'concordance',
      column: delimiter('\u0014'),
      quote: delimiter('þ'),
      newline: delimiter('®'),
      multiValue: delimiter(';'),
      nestedValue: delimiter('\\'),
      header: HEADER,
      misdecodeSuspected: true,
      parserIssues: [],
    },
    issues: [],
    missingColumns: [],
    newColumns: [],
    newFields: [],
    rows: Array.from({ length: 20 }, (_, i) => ({
      rowNumber: i + 1,
      lineNumber: i + 2,
      controlNumber: `IMP${i + 1}`,
      rejected: false,
      parserIssues: [],
      errorCount: 0,
      cells: [
        {
          column: 'BEGDOC',
          target: 'Control Number',
          raw: `IMP${i + 1}`,
          value: null,
          error: null,
          warnings: [],
        },
      ],
    })),
  };
}

function preflight(blocking: boolean) {
  return {
    preflightId: 'pf-1',
    rowsRead: '20',
    errorCount: blocking ? 1 : 0,
    warningCount: 1,
    blocking,
    issueCounts: [
      ...(blocking ? [{ code: 'KEY_EXISTS', severity: 'error', count: 1 }] : []),
      { code: 'NATIVE_MISSING', severity: 'warning', count: '1' },
    ],
    issues: [
      {
        row: 3,
        controlNumber: 'IMP3',
        column: 'NATIVEPATH',
        code: 'NATIVE_MISSING',
        severity: 'warning',
        message: 'Missing native.',
      },
    ],
    mode: 'append',
  };
}

function importResource(id: string, status: string, indexed: 'indexing' | 'current', errored = 0) {
  return {
    importId: id,
    workspaceId: 'ws-1',
    name: 'VOL001.dat 2026-10-04',
    mode: 'append',
    sourceFileName: 'VOL001.dat',
    sourceSize: 10,
    sourceSha256: 'x',
    profileId: null,
    codingOverlayFieldIds: [],
    report: {
      rowsRead: 20,
      rowsImported: 20 - errored,
      rowsOverlaid: 0,
      rowsSkipped: 0,
      rowsErrored: errored,
      fieldsCreated: 0,
      choicesCreated: 0,
    },
    job: {
      jobId: `job-${id}`,
      status,
      statusReason: null,
      committed: {
        chunksTotal: 2,
        chunksCommitted: status === 'running' ? 1 : 2,
        chunksFailed: 0,
        chunksCancelled: 0,
        chunksPending: 0,
      },
      indexed: {
        indexTasksApplied: indexed === 'current' ? 2 : 0,
        indexTasksTotal: 2,
        state: indexed,
      },
    },
    createdAt: '2026-10-04T09:00:00Z',
    completedAt: null,
    summary: {
      importId: id,
      name: 'VOL001.dat 2026-10-04',
      mode: 'append',
      sourceFileName: 'VOL001.dat',
      status,
      final: status !== 'running' && status !== 'created',
      startedAt: '2026-10-04T09:00:00Z',
      completedAt: null,
      elapsedSeconds: 125,
      rows: {
        read: 20,
        imported: 20 - errored,
        overlaid: 0,
        skipped: 0,
        errored,
        withWarnings: 0,
      },
      natives: { linked: 20 - errored, missing: 3 },
      text: { linked: 20 - errored, missing: 0, truncated: 0 },
      images: { documentsLinked: 0, documentsWithoutImages: 0, pagesLinked: 0, pagesMissing: 0 },
      families: { built: 2, orphans: 0 },
      fieldsCreated: 1,
      choicesCreated: 0,
      errorFileRows: errored,
      issueCounts: errored ? [{ code: 'DATE_UNPARSEABLE', severity: 'error', count: errored }] : [],
    },
  };
}

describe('Imports section (E08-T08)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let detailReads: number;

  async function setup(
    options: {
      permissions?: string[];
      blocking?: boolean;
      start?: (req: HttpRequest<unknown>) => FakeResponse;
    } = {},
  ): Promise<void> {
    detailReads = 0;
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: options.permissions ?? [
            PERMISSIONS.documentView,
            PERMISSIONS.importRun,
            PERMISSIONS.importOverlay,
          ],
        },
      })
      .on('GET', `${WS}/import-targets`, {
        body: {
          items: [
            {
              target: { kind: 'field', fieldId: 1, fieldName: 'Control Number' },
              label: 'Control Number',
              type: 'keyword',
              isMultiValue: false,
              isStructural: true,
              isCodingField: false,
              isSecurityAffecting: false,
              autoMapEligible: true,
              aliases: ['BEGDOC'],
            },
            {
              target: { kind: 'field', fieldId: 100, fieldName: 'Responsiveness' },
              label: 'Responsiveness',
              type: 'singleChoice',
              isMultiValue: false,
              isStructural: false,
              isCodingField: true,
              isSecurityAffecting: false,
              autoMapEligible: false,
              aliases: [],
            },
          ],
          nextCursor: null,
          total: { value: 2, relation: 'eq' },
        },
      })
      .on('GET', `${WS}/import-profiles`, {
        body: {
          items: [
            {
              profileId: 'p-1',
              name: 'Vendor A',
              description: null,
              mode: 'overlay',
              columnCount: 1,
              updatedAt: null,
              version: 1,
            },
          ],
          nextCursor: null,
          total: { value: 1, relation: 'eq' },
        },
      })
      .on('GET', `${WS}/import-profiles/p-1`, {
        body: {
          profileId: 'p-1',
          name: 'Vendor A',
          definition: {
            mode: 'overlay',
            loadFile: { delimiters: 'csv' },
            overlay: { blankValuesOverwrite: true },
            columns: [],
          },
        },
      })
      .on('POST', `${WS}/import-profiles`, (req) => ({
        status: 201,
        body: { profileId: 'p-2', name: (req.body as { name: string }).name, definition: {} },
      }))
      .on('POST', `${WS}/import-mapping-previews`, (req) => ({
        body: preview(JSON.parse(String((req.body as FormData).get('request')))),
      }))
      .on('POST', `${WS}/imports/preflight`, { body: preflight(!!options.blocking) })
      .on(
        'POST',
        `${WS}/imports`,
        options.start ?? { status: 202, body: importResource('imp-9', 'created', 'indexing') },
      )
      .on('GET', `${WS}/imports`, {
        body: {
          items: [importResource('imp-1', 'completedWithErrors', 'current', 2)],
          nextCursor: null,
          total: { value: 1, relation: 'eq' },
        },
      })
      .on('GET', `${WS}/imports/imp-9`, () => {
        detailReads++;
        return {
          body:
            detailReads === 1
              ? importResource('imp-9', 'running', 'indexing')
              : importResource(
                  'imp-9',
                  'completedWithErrors',
                  detailReads > 2 ? 'current' : 'indexing',
                  1,
                ),
        };
      })
      .on('GET', `${WS}/imports/imp-run`, {
        body: importResource('imp-run', 'running', 'indexing'),
      })
      .on('GET', `${WS}/imports/imp-9/errors`, {
        body: {
          items: [
            {
              row: 5,
              line: 6,
              severity: 'error',
              controlNumber: 'IMP5',
              column: 'DATESENT',
              code: 'DATE_UNPARSEABLE',
              message: 'Bad date.',
            },
          ],
          nextCursor: null,
          total: { value: 1, relation: 'eq' },
        },
      });
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: IMPORT_POLL_MS, useValue: 1 },
      ],
    });
    localStorage.clear();
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.replace(/\s+/g, ' ').trim() === name,
    )!;
  const heading = () =>
    root().querySelector('h2#import-step-heading')!.textContent!.replace(/\s+/g, ' ').trim();
  const requests = (method: string, url: string) =>
    api.requests.filter((r) => r.method === method && r.url === url);
  const requestJson = (req: HttpRequest<unknown>) =>
    JSON.parse(String((req.body as FormData).get('request')));

  async function chooseDat(): Promise<void> {
    const input = root().querySelector<HTMLInputElement>('#import-dat')!;
    const dat = new File(['þBEGDOCþ\u0014þCUSTODIANþ\u0014þNOTESþ\r\n'], 'VOL001.dat');
    Object.defineProperty(input, 'files', { value: [dat] });
    input.dispatchEvent(new Event('change'));
    await settle(5);
  }

  async function next(): Promise<void> {
    button(heading().includes('Validate') ? 'Start import' : 'Continue').click();
    await settle(5);
  }

  it('walks every step: detection, override, mapping, pre-flight with warnings acknowledged, start and progress', async () => {
    await setup();
    await go('/w/ws-1/imports/new');
    expect(text()).toContain('New Import');
    expect(heading()).toBe('Step 1 of 5 Source & mode');
    await expectNoAxeViolations(root());

    // Step 1 blocks without a DAT; a volume folder outside the share is refused.
    await next();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('Choose the load file');
    await chooseDat();
    const volume = root().querySelector<HTMLInputElement>('input[placeholder="matter-a/VOL001"]')!;
    volume.value = '/etc';
    volume.dispatchEvent(new Event('input'));
    await settle();
    expect(text()).toContain('not an absolute path');
    volume.value = 'matter-a/VOL001';
    volume.dispatchEvent(new Event('input'));
    await next();

    // Step 2: detected delimiters with glyph and code; the 20-row preview; the encoding override re-previews.
    expect(heading()).toBe('Step 2 of 5 File format');
    expect(text()).toContain('DC4 (20)');
    expect(text()).toContain('þ (254)');
    expect(text()).toContain('Detected delimiters: Concordance-style (DC4 / þ / ®).');
    expect(text()).toContain('utf-8, detected from the content');
    expect(text()).toContain('wrongly decoded');
    expect(
      root().querySelectorAll('[aria-label="Preview of the first rows"] tbody tr'),
    ).toHaveLength(20);
    const firstPreview = requestJson(requests('POST', `${WS}/import-mapping-previews`)[0]);
    expect(firstPreview).toMatchObject({
      rows: 20,
      autoMap: true,
      sampleIsPartial: false,
      profile: { loadFile: { delimiters: 'concordance' } },
    });
    const encoding = [...root().querySelectorAll('select')].find((s) =>
      s.closest('opp-select')?.textContent?.includes('Load file encoding'),
    )!;
    encoding.value = 'windows-1252';
    encoding.dispatchEvent(new Event('change'));
    await settle(5);
    const last = requests('POST', `${WS}/import-mapping-previews`).at(-1)!;
    expect(requestJson(last).profile.loadFile.datEncoding).toBe('windows-1252');
    await expectNoAxeViolations(root());
    await next();

    // Step 3: target + type per mapped column; unmapped explicit; coding fields offered only with coding overlay.
    expect(heading()).toBe('Step 3 of 5 Field mapping');
    expect(text()).toContain('2 of 3 columns are imported. Not imported: NOTES.');
    const rows = [...root().querySelectorAll('[aria-label="Field mapping"] tbody tr')];
    expect(rows[0].textContent).toContain('Mapped, matched by alias BEGDOC');
    expect(rows[0].textContent).toContain('Short Text');
    expect(rows[1].textContent).toContain('2 unreadable in preview');
    expect(rows[2].textContent).toContain('Not mapped: not imported');
    const coding = root().querySelector<HTMLOptionElement>(
      'select[aria-label="Workspace field for NOTES"] option[value="f:100"]',
    )!;
    expect(coding.disabled).toBe(true);
    const notes = root().querySelector<HTMLSelectElement>(
      'select[aria-label="Workspace field for NOTES"]',
    )!;
    notes.value = 'ignore';
    notes.dispatchEvent(new Event('change'));
    await settle(5);
    expect(
      root().querySelectorAll('[aria-label="Field mapping"] tbody tr')[2].textContent,
    ).toContain('Do not import');
    await expectNoAxeViolations(root());
    await next();

    // Step 4: natives, text & images; step 5: pre-flight.
    expect(heading()).toBe('Step 4 of 5 Natives, text & images');
    expect(text()).toContain('No OPT file was chosen');
    await next();
    expect(heading()).toBe('Step 5 of 5 Validate & run');
    expect(requests('POST', `${WS}/imports/preflight`)).toHaveLength(1);
    expect(text()).toContain('NATIVE_MISSING');
    const download = [...root().querySelectorAll<HTMLAnchorElement>('a')].find((a) =>
      a.textContent?.includes('Download all issues'),
    )!;
    expect(download.getAttribute('href')).toBe(`${WS}/imports/preflight/pf-1/issues`);
    await expectNoAxeViolations(root());

    // Warnings must be acknowledged.
    await next();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain(
      'Acknowledge the warnings',
    );
    expect(requests('POST', `${WS}/imports`)).toHaveLength(0);
    const ack = [...root().querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].at(-1)!;
    ack.click();
    await settle();
    await next();
    await settle(5);

    const [start] = requests('POST', `${WS}/imports`);
    expect(start.headers.get('Idempotency-Key')).toBeTruthy();
    expect((start.body as FormData).get('file')).toBeInstanceOf(File);
    expect(requestJson(start)).toMatchObject({
      name: null,
      mode: 'append',
      autoMap: false,
      profile: {
        mode: 'append',
        paths: { volumeRoot: 'matter-a/VOL001' },
        overlay: {
          keyField: 'ControlNumber',
          blankValuesOverwrite: false,
          multiValue: 'replace',
          allowCodingFields: false,
        },
        columns: [
          { column: 'BEGDOC', ignore: false, targets: [{ kind: 'field', fieldId: 1 }] },
          { column: 'NOTES', ignore: true, targets: [] },
        ],
      },
    });

    // The import page follows the job until it is searchable, then shows the report and downloads.
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/imports/imp-9');
    for (let i = 0; i < 20 && detailReads < 3; i++) await settle(5);
    await settle(5);
    expect(text()).toContain('Completed with errors');
    const searchable = root().querySelector('[role="progressbar"][aria-label="Searchable"]')!;
    expect(searchable.getAttribute('aria-valuenow')).toBe('2');
    const links = [...root().querySelectorAll<HTMLAnchorElement>('a')];
    expect(
      links.find((a) => a.textContent?.includes('Import report (CSV)'))?.getAttribute('href'),
    ).toBe(`${WS}/imports/imp-9/report.csv`);
    expect(links.find((a) => a.textContent?.trim() === 'Error file')?.getAttribute('href')).toBe(
      `${WS}/imports/imp-9/error-file`,
    );
    expect(links.find((a) => a.textContent?.includes('View in Jobs'))?.getAttribute('href')).toBe(
      '/w/ws-1/jobs/job-imp-9',
    );
    expect(
      links.find((a) => a.textContent?.includes('Re-import corrected'))?.getAttribute('href'),
    ).toBe('/w/ws-1/imports/new?from=imp-9');
    expect(text()).toContain('DATE_UNPARSEABLE');
    // Finished: the full report summary, grouped and in plain language (Q-71).
    const groups = [...root().querySelectorAll('.imports__report-group')];
    expect(groups.map((g) => g.querySelector('h3')?.textContent?.trim())).toEqual([
      'Rows',
      'Natives and text',
      'Images',
      'Families',
      'Fields',
    ]);
    const factOf = (label: string) =>
      [...root().querySelectorAll('.imports__report-list div')].find(
        (d) => d.querySelector('dt')?.textContent?.trim() === label,
      );
    expect(factOf('New documents loaded')?.querySelector('dd')?.textContent?.trim()).toBe('19');
    expect(factOf('Natives missing')?.textContent).toContain('Needs attention');
    expect(factOf('Natives stored')?.textContent).not.toContain('Needs attention');
    expect(text()).toContain('No page images were loaded by this import.');
    expect(text()).toContain('Took 2 minutes 5 seconds.');
    expect(text()).toContain('1 row is in the error file.');
    expect(
      root().querySelector('[role="region"][aria-label="Issues by type"]')?.textContent,
    ).toContain('DATE_UNPARSEABLE');
    const reads = detailReads;
    await settle(5);
    expect(detailReads).toBe(reads);
    await expectNoAxeViolations(root());
  }, 30_000);

  it('a running import shows the live counters, not the report summary (Q-71)', async () => {
    await setup();
    await go('/w/ws-1/imports/imp-run');
    expect(text()).toContain('Rows read');
    expect(text()).toContain('The report downloads are available when the import finishes.');
    expect(root().querySelector('.imports__report-groups')).toBeNull();
    expect(root().querySelector('[aria-label="Issues by type"]')).toBeNull();
  });

  it('pre-flight errors block the start', async () => {
    await setup({ blocking: true });
    await go('/w/ws-1/imports/new');
    await chooseDat();
    for (let i = 0; i < 4; i++) await next();
    expect(heading()).toContain('Validate & run');
    expect(text()).toContain('1 error(s) block this import');
    expect(root().querySelectorAll('input[type="checkbox"]')).toHaveLength(0);
    await next();
    expect(root().querySelector('[role="alert"].wizard__blocked')?.textContent).toContain(
      'Fix the errors',
    );
    expect(requests('POST', `${WS}/imports`)).toHaveLength(0);
  });

  it('a saved profile pre-fills mode, delimiters and overlay options; overlay settings then appear', async () => {
    await setup();
    await go('/w/ws-1/imports/new');
    await chooseDat();
    const profile = [...root().querySelectorAll('select')].find((s) =>
      s.closest('opp-select')?.textContent?.includes('Import profile'),
    )!;
    profile.value = 'p-1';
    profile.dispatchEvent(new Event('change'));
    await settle(5);
    expect(
      root().querySelector<HTMLInputElement>('input[name="import-mode"][value="overlay"]')!.checked,
    ).toBe(true);
    expect(heading()).toBe('Step 1 of 6 Source & mode');
    await next();
    expect(
      requestJson(requests('POST', `${WS}/import-mapping-previews`).at(-1)!).profile,
    ).toMatchObject({
      mode: 'overlay',
      loadFile: { delimiters: 'csv' },
      overlay: { blankValuesOverwrite: true },
    });
    await next();
    await next();
    expect(heading()).toBe('Step 4 of 6 Overlay settings');
    const clear = [...root().querySelectorAll<HTMLInputElement>('input[name="overlay-blank"]')][1];
    expect(clear.checked).toBe(true);
  });

  it('without overlay permission only Append can be chosen', async () => {
    await setup({ permissions: [PERMISSIONS.documentView, PERMISSIONS.importRun] });
    await go('/w/ws-1/imports/new');
    const radios = [...root().querySelectorAll<HTMLInputElement>('input[name="import-mode"]')];
    expect(radios.map((r) => r.disabled)).toEqual([false, true, true]);
    expect(text()).toContain('overlays need overlay permission');
  });

  it('saves the mapping as an import profile', async () => {
    await setup();
    await go('/w/ws-1/imports/new');
    await chooseDat();
    await next();
    await next();
    button('Save as Import profile…').click();
    await settle();
    button('Save profile').click();
    await settle();
    expect(text()).toContain('Enter a name for the import profile.');
    const name = [...root().querySelectorAll<HTMLInputElement>('input')].find((i) =>
      i.closest('opp-text-field')?.textContent?.includes('Import profile name'),
    )!;
    name.value = 'Vendor B';
    name.dispatchEvent(new Event('input'));
    button('Save profile').click();
    await settle(5);
    const [save] = requests('POST', `${WS}/import-profiles`);
    expect(save.body).toMatchObject({
      name: 'Vendor B',
      definition: { mode: 'append', unmappedColumns: 'ignore' },
    });
  });

  it('lists the import history with status, search state and counts', async () => {
    await setup();
    await go('/w/ws-1/imports');
    expect(text()).toContain('VOL001.dat 2026-10-04');
    expect(text()).toContain('Completed with errors');
    expect(text()).toContain('Searchable');
    const link = root().querySelector<HTMLAnchorElement>('tbody a')!;
    expect(link.getAttribute('href')).toBe('/w/ws-1/imports/imp-1');
    expect(
      root().querySelector<HTMLAnchorElement>('a[href="/w/ws-1/imports/new"]')?.textContent,
    ).toContain('New Import');
    await expectNoAxeViolations(root());
  });

  it('the section is not available without Import.Run', async () => {
    await setup({ permissions: [PERMISSIONS.documentView] });
    await go('/w/ws-1/imports/new');
    expect(text()).toContain('Not available');
  });
});
