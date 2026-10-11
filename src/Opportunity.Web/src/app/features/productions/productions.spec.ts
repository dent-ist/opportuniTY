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
import {
  batesPreview,
  controlNumberQuery,
  defaultProductionForm,
  formFromSpecification,
  formProblems,
  nextAvailableStart,
  stageOf,
  toSpecification,
} from './production-model';

const WS = '/api/v1/workspaces/ws-1';
const ME = { userId: 'u-1', displayName: 'Pat Production', email: 'p@example.test', groups: [] };
const SHA = 'e'.repeat(64);

type Overrides = Record<string, unknown>;

function production(id: string, status: string, overrides: Overrides = {}) {
  const allocated = status !== 'draft' || overrides['allocated'] === true;
  return {
    productionId: id,
    workspaceId: 'ws-1',
    lineageId: `lin-${id}`,
    name: id === 'p-1' ? 'Volume 1' : 'Volume 2',
    version: 1,
    status,
    snapshotId: `snap-${id}`,
    specification: {
      bates: { prefix: 'ACME', startNumber: id === 'p-1' ? 1 : 121, padding: 7, level: 'page' },
      withheldDocuments: 'placeholder',
      images: { format: 'tiffG4', dpi: 300, colorFileTypes: ['jpg'] },
      includeText: true,
      fileTypeRules: [{ extensions: ['xlsx'], output: 'native' }],
      endorsements: {
        items: [
          { position: 'bottomLeft', template: '{confidentiality}' },
          { position: 'bottomRight', template: '{bates}' },
        ],
      },
      placeholders: { withheld: 'Withheld – Privileged' },
      loadFile: { delimiters: 'concordance', encoding: 'utf-8', volume: { prefix: 'ACME_VOL' } },
    },
    specificationSha256: SHA,
    bates: allocated
      ? {
          state: 'allocated',
          first: id === 'p-1' ? 'ACME0000001' : 'ACME0000121',
          last: id === 'p-1' ? 'ACME0000120' : 'ACME0000180',
          firstNumber: id === 'p-1' ? 1 : 121,
          lastNumber: id === 'p-1' ? 120 : 180,
          documents: id === 'p-1' ? 30 : 12,
          numbers: id === 'p-1' ? 120 : 60,
          integrity: {
            passed: true,
            documents: 12,
            numbers: 60,
            gaps: 0,
            placeholders: 1,
            nativeSlipSheets: 0,
            problems: [],
          },
          reason: null,
          assignmentsSha256: SHA,
        }
      : {
          state: 'none',
          first: null,
          last: null,
          firstNumber: null,
          lastNumber: null,
          documents: null,
          numbers: null,
          integrity: null,
          reason: null,
          assignmentsSha256: null,
        },
    batesJob: null,
    manifest: status === 'finalized' ? '{"production":"p-1"}' : null,
    manifestSha256: status === 'finalized' ? SHA : null,
    createdBy: 'u-1',
    createdAt: '2026-10-01T09:00:00Z',
    modifiedAt: '2026-10-01T09:00:00Z',
    finalizedAt: status === 'finalized' ? '2026-10-02T12:00:00Z' : null,
    voidedAt: null,
    voidReason: null,
    discardedAt: null,
    ...overrides,
  };
}

function snapshot(id: string, count: number, pages: number) {
  return {
    snapshotId: id,
    name: 'Frozen',
    purpose: 'production',
    status: 'ready',
    statusReason: null,
    documentCount: count,
    pageCount: pages,
    inclusionCounts: {},
    createdAt: '2026-10-01T09:00:00Z',
  };
}

function check(name: string, status: string, overridable = false, documents = 0) {
  return {
    check: name,
    title: name === 'renderFailure' ? 'Pages that could not be rendered' : `Check ${name}`,
    severity: status === 'warning' ? 'warning' : 'blocking',
    status,
    documents,
    overridable,
    overrideReason: null,
    remedy: 'Fix the documents and run the checks again.',
  };
}

function qc(outcome: string, checks: unknown[], purpose = 'check') {
  return {
    productionId: 'p-2',
    qcRunId: 'qc-1',
    purpose,
    outcome,
    runAt: '2026-10-03T08:00:00Z',
    runBy: 'u-1',
    reportSha256: SHA,
    warningsAcknowledged: false,
    checks,
  };
}

function volume(id: string, sha: string, createdAt: string) {
  return {
    volumeId: id,
    productionId: 'p-1',
    workspaceId: 'ws-1',
    volume: 'ACME_VOL001',
    status: 'completed',
    statusReason: null,
    report: {
      documents: 30,
      images: 110,
      natives: 2,
      texts: 30,
      files: 145,
      totalBytes: 5_000_000,
      manifestSha256: sha,
    },
    verification: {
      status: 'passed',
      documents: 30,
      pages: 110,
      boxes: 4,
      failures: 0,
      reportSha256: SHA,
    },
    job: {
      jobId: `job-${id}`,
      status: 'completed',
      committed: { chunksTotal: 2, chunksCommitted: 2 },
    },
    createdBy: 'u-1',
    createdAt,
    completedAt: createdAt,
  };
}

describe('Productions (E12-T08)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(configure: (api: FakeApi) => void = () => undefined, finalize = true) {
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
            PERMISSIONS.productionCreate,
            PERMISSIONS.privilegeLogGenerate,
            ...(finalize ? [PERMISSIONS.productionFinalize] : []),
          ],
        },
      })
      .on('GET', `${WS}/productions`, {
        body: {
          items: [production('p-2', 'draft', { allocated: true }), production('p-1', 'finalized')],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/productions/p-1`, {
        body: production('p-1', 'finalized'),
        headers: { ETag: '"7"' },
      })
      .on('GET', `${WS}/productions/p-2`, {
        body: production('p-2', 'draft', { allocated: true }),
        headers: { ETag: '"3"' },
      })
      .on('GET', `${WS}/snapshots/snap-p-1`, { body: snapshot('snap-p-1', 30, 110) })
      .on('GET', `${WS}/snapshots/snap-p-2`, { body: snapshot('snap-p-2', 12, 55) })
      .on('GET', `${WS}/productions/p-1/qc`, {
        body: qc('passed', [check('batesOverlap', 'passed')], 'finalization'),
      })
      .on('GET', `${WS}/productions/p-2/qc`, { status: 404, body: { status: 404 } })
      .on('GET', `${WS}/redaction-sets`, {
        body: { items: [{ redactionSetId: 'rs-1', name: 'Privilege', retired: false }] },
      })
      .on('GET', `${WS}/saved-searches`, {
        body: { items: [{ savedSearchId: 'ss-1', name: 'Responsive', lastHitCount: 12 }] },
      })
      .on('GET', `${WS}/productions/p-1/volumes`, {
        body: {
          items: [
            volume('vol-2', SHA, '2026-10-04T10:00:00Z'),
            volume('vol-1', SHA, '2026-10-03T10:00:00Z'),
          ],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/productions/p-2/documents`, {
        body: {
          items: [
            {
              documentId: 'd-1',
              sequence: 1,
              controlNumber: 'DOC-1',
              prodBegBates: 'ACME0000121',
              prodEndBates: 'ACME0000125',
              prodBegAttach: 'ACME0000121',
              prodEndAttach: 'ACME0000130',
              numbers: 5,
              output: 'image',
              designation: 'Confidential',
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

  async function settle(rounds = 10): Promise<void> {
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
  const input = (label: string) => {
    const l = [...root().querySelectorAll('label')].find((x) => text(x).startsWith(label))!;
    return root().querySelector<HTMLInputElement>(`#${l.getAttribute('for')}`)!;
  };
  async function type(label: string, value: string) {
    const el = input(label);
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle(2);
  }

  it('lists productions with prefix, Bates range, counts, stage and finalized date', async () => {
    await setup();
    await go('/w/ws-1/productions');
    expect(text(root().querySelector('h1')!)).toBe('Productions');
    const rows = [...root().querySelectorAll('tbody tr')];
    expect(text(rows[1])).toContain('Volume 1');
    expect(text(rows[1])).toContain('ACME0000001 – ACME0000120');
    expect(text(rows[1])).toContain('Finalized');
    expect(text(rows[1])).toContain('Oct 2, 2026');
    expect(text(rows[1])).toContain('snap-p-1');
    expect(text(rows[0])).toContain('Bates assigned');
    await expectNoAxeViolations(root());
  });

  it('creates a draft from a frozen saved search with a Bates preview and the next free number', async () => {
    let posted: HttpRequest<unknown> | null = null;
    await setup((a) =>
      a
        .on('POST', `${WS}/snapshots`, { status: 201, body: snapshot('snap-new', 12, 40) })
        .on('POST', `${WS}/productions`, (req) => {
          posted = req;
          return {
            status: 201,
            body: production('p-9', 'draft'),
            headers: { ETag: '"1"' },
          };
        })
        .on('GET', `${WS}/productions/p-9`, {
          body: production('p-9', 'draft'),
          headers: { ETag: '"1"' },
        })
        .on('GET', `${WS}/productions/p-9/qc`, { status: 404, body: { status: 404 } })
        .on('GET', `${WS}/snapshots/snap-p-9`, { body: snapshot('snap-p-9', 12, 40) }),
    );
    await go('/w/ws-1/productions/new');
    button('Create draft').click();
    await settle();
    expect(text()).toContain('Freeze the documents to produce first.');

    button('Freeze set').click();
    await settle();
    expect(api.requests.find((r) => r.url === `${WS}/snapshots`)!.body).toMatchObject({
      purpose: 'production',
      savedSearchId: 'ss-1',
    });
    await type('Bates prefix', 'ACME');
    // ACME0000001 – ACME0000180 are taken: the next free number is offered.
    expect(text()).toContain('The next available number for this prefix is 181.');
    button('Use 181').click();
    await settle();
    const preview = root().querySelector('[aria-label="Bates preview"]')!;
    expect(text(preview)).toContain('ACME0000181');
    // Page level: twelve documents take at least twelve numbers.
    expect(text(preview)).toContain('at least ACME0000192');
    expect(text(preview)).toContain('at least 12 numbers');

    button('Create draft').click();
    await settle();
    expect(posted!.body).toMatchObject({
      snapshotId: 'snap-new',
      specification: {
        bates: { prefix: 'ACME', startNumber: 181, padding: 7, level: 'page' },
        withheldDocuments: 'placeholder',
        endorsements: {
          items: [
            { position: 'bottomLeft', template: '{confidentiality}' },
            { position: 'bottomRight', template: '{bates}' },
          ],
        },
      },
    });
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/productions/p-9');
  });

  it('copies the settings of a production into a new draft that continues the numbering', async () => {
    await setup();
    await go('/w/ws-1/productions/new?copyFrom=p-1');
    expect(text()).toContain('Settings copied from Volume 1');
    expect(input('Bates prefix').value).toBe('ACME');
    expect(input('Start number').value).toBe('181');
    expect(input('Production name').value).toBe('Volume 1 (copy)');
  });

  it('shows the draft header with frozen set ID and count, and its documents with Bates numbers', async () => {
    await setup();
    await go('/w/ws-1/productions/p-2?tab=documents');
    expect(text(root().querySelector('h1')!)).toBe('Volume 2');
    const summary = root().querySelector('[aria-label="Production summary"]')!;
    expect(text(summary)).toContain('snap-p-2');
    expect(text(summary)).toContain('12');
    expect(text()).toContain('ACME0000121 – ACME0000180');
    const row = root().querySelector('[aria-label="Production documents"] tbody tr')!;
    expect(text(row)).toContain('DOC-1');
    expect(text(row)).toContain('ACME0000125');
    const open = [...root().querySelectorAll<HTMLAnchorElement>('a')].find((a) =>
      text(a).startsWith('Open listed documents'),
    )!;
    const href = decodeURIComponent(open.getAttribute('href')!);
    expect(href).toContain('/w/ws-1/documents?listQuery=controlnumber:("DOC-1")');
    await expectNoAxeViolations(root());
  });

  it('runs QC, blocks on failures and finalizes with a reasoned override and acknowledged warnings', async () => {
    let finalized: HttpRequest<unknown> | null = null;
    const blocked = qc('blocked', [
      check('renderFailure', 'failed', true, 2),
      check('textMissing', 'warning', false, 1),
      check('batesOverlap', 'passed'),
    ]);
    await setup((a) =>
      a
        .on('POST', `${WS}/productions/p-2/qc`, { body: blocked })
        .on('GET', `${WS}/productions/p-2/qc/exceptions`, {
          body: {
            qcRunId: 'qc-1',
            restricted: 1,
            nextCursor: null,
            items: [
              {
                check: 'renderFailure',
                documentId: 'd-1',
                controlNumber: 'DOC-1',
                prodBegBates: 'ACME0000121',
                prodEndBates: 'ACME0000125',
                sequence: 1,
                detail: 'Page 3 failed',
              },
            ],
          },
        })
        .on('POST', `${WS}/productions/p-2/finalize`, (req) => {
          finalized = req;
          return {
            body: production('p-2', 'finalized'),
            headers: { ETag: '"4"' },
          };
        }),
    );
    vi.spyOn(TestBed.inject(DialogService), 'confirm').mockResolvedValue(true);
    await go('/w/ws-1/productions/p-2?tab=qc');
    expect(text()).toContain('ACME0000121');
    button('Run QC checks').click();
    await settle();
    expect(text()).toContain('Pages that could not be rendered');
    expect(text()).toContain('Failed');
    // The QC report downloads from the gateway.
    expect(
      root().querySelector(`a[href="${WS}/productions/p-2/qc/report?format=csv"]`),
    ).not.toBeNull();

    button('Show documents').click();
    await settle();
    expect(text()).toContain('Page 3 failed');
    expect(text()).toContain('1 more documents you cannot view are counted, not listed.');

    // An override without a reason is refused before anything is sent.
    root().querySelectorAll<HTMLInputElement>('.dl__check input[type=checkbox]')[0].click();
    await settle();
    button('Finalize production').click();
    await settle();
    expect(text()).toContain('Give a reason for every check you override.');
    await type('Reason for overriding', 'Spreadsheets produced natively instead');
    const ack = [...root().querySelectorAll('label')].find((l) =>
      text(l).startsWith('I have reviewed the 1 warning'),
    )!;
    ack.querySelector('input')!.click();
    await settle();
    button('Finalize production').click();
    await settle();
    expect(finalized!.headers.get('If-Match')).toBe('"3"');
    expect(finalized!.body).toEqual({
      acknowledgeWarnings: true,
      qcOverrides: [{ check: 'renderFailure', reason: 'Spreadsheets produced natively instead' }],
    });
  });

  it('shows the QC report a refused finalization returns', async () => {
    await setup((a) =>
      a
        .on('GET', `${WS}/productions/p-2/qc`, {
          body: qc('passed', [check('batesOverlap', 'passed')]),
        })
        .on('POST', `${WS}/productions/p-2/finalize`, {
          status: 409,
          body: {
            title: 'Documents are withheld for privilege',
            detail: '2 documents are withheld.',
            status: 409,
            reason: 'PRIVILEGE_WITHHELD',
            qc: qc('blocked', [check('withheldWithoutPlaceholder', 'failed', false, 2)]),
          },
        }),
    );
    vi.spyOn(TestBed.inject(DialogService), 'confirm').mockResolvedValue(true);
    await go('/w/ws-1/productions/p-2?tab=qc');
    expect(text()).toContain('QC passed');
    button('Finalize production').click();
    await settle();
    expect(text()).toContain('Documents are withheld for privilege: 2 documents are withheld.');
    expect(text()).toContain('Check withheldWithoutPlaceholder');
    expect(text()).toContain('cannot be overridden');
  });

  it('shows the output of a finalized production and re-runs its volume with the same specification', async () => {
    let started = 0;
    await setup((a) =>
      a.on('POST', `${WS}/productions/p-1/volumes`, (req) => {
        started++;
        expect(req.headers.get('Idempotency-Key')).toBeTruthy();
        return { status: 202, body: volume('vol-3', SHA, '2026-10-05T10:00:00Z') };
      }),
    );
    vi.spyOn(TestBed.inject(DialogService), 'confirm').mockResolvedValue(true);
    await go('/w/ws-1/productions/p-1?tab=output');
    expect(text()).toContain('Finalized');
    expect(text()).toContain(SHA);
    expect(text()).toContain('Identical to the first run');
    const hrefs = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].map((a) =>
      a.getAttribute('href'),
    );
    expect(hrefs).toContain(`${WS}/productions/p-1/qc/report?format=pdf`);
    expect(hrefs).toContain(`${WS}/productions/p-1/volumes/vol-2/package`);
    expect(hrefs).toContain(`${WS}/productions/p-1/volumes/vol-1/verification-report`);
    button('Re-run with same specification').click();
    await settle();
    expect(started).toBe(1);
    await expectNoAxeViolations(root());
  });

  it('shows a finalized production its frozen specification read-only', async () => {
    await setup();
    await go('/w/ws-1/productions/p-1');
    expect(text()).toContain('Frozen specification');
    expect(text()).toContain('Prefix ACME, start 1, 7 digits, page level');
    expect(text()).toContain(SHA);
    expect(root().querySelector('input[type=text], input:not([type])')).toBeNull();
    const copy = [...root().querySelectorAll<HTMLAnchorElement>('a')].find((a) =>
      text(a).startsWith('Copy settings'),
    )!;
    expect(copy.getAttribute('href')).toBe('/w/ws-1/productions/new?copyFrom=p-1');
  });
});

describe('production model', () => {
  it('previews the Bates range', () => {
    const form = { ...defaultProductionForm(), prefix: 'ACME', startNumber: '5' };
    expect(batesPreview(form, 10)).toEqual({
      first: 'ACME0000005',
      last: 'ACME0000014',
      count: 10,
      atLeast: true,
    });
    expect(batesPreview({ ...form, level: 'document', suffix: '-C' }, null)).toEqual({
      first: 'ACME0000005-C',
      last: null,
      count: null,
      atLeast: false,
    });
    expect(batesPreview({ ...form, prefix: '' }, 3)).toBeNull();
  });

  it('offers the number after the highest one any production of the prefix holds', () => {
    const list = [
      production('p-1', 'finalized'),
      production('p-2', 'discarded', { allocated: true }),
    ] as never[];
    expect(nextAvailableStart('acme', list)).toBe(121);
    expect(nextAvailableStart('OTHER', list)).toBe(1);
  });

  it('keeps the settings it does not edit and checks the form', () => {
    const p = production('p-1', 'finalized');
    const form = formFromSpecification('Volume 1', p.specification as never);
    const spec = toSpecification({ ...form, startNumber: '200' }, p.specification as never);
    expect(spec.fileTypeRules).toEqual([{ extensions: ['xlsx'], output: 'native' }]);
    expect(spec.bates.startNumber).toBe(200);
    expect(formProblems({ ...form, padding: '2', startNumber: '1000' })).toEqual([
      'Bates start: more digits than the padding allows.',
    ]);
  });

  it('names the stage and builds an Open in Documents query', () => {
    expect(stageOf(production('p-2', 'draft') as never).label).toBe('Draft');
    expect(
      stageOf(production('p-2', 'draft', { allocated: true }) as never, qc('passed', []) as never)
        .label,
    ).toBe('QC passed');
    expect(controlNumberQuery(['A-1', null, 'B "2"', 'A-1'])).toBe(
      'controlnumber:("A-1" OR "B \\"2\\"")',
    );
  });
});
