import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { localDateTime, parseDocumentIds } from './defensibility-api';

const WS = '/api/v1/workspaces/ws-1';
const DOC = '0199c3a0-0000-7000-8000-00000000d001';
const HASH = 'a'.repeat(64);

function statement(report: string) {
  return {
    report,
    asOf: '2026-10-10T12:00:00Z',
    snapshotId: null,
    searchGeneration: 42,
    generatedAt: '2026-10-11T09:00:00Z',
    generatedBy: 'Alex Auditor',
    fingerprint: 'f'.repeat(64),
    auditEventId: '0199c3a0-0000-7000-8000-00000000e001',
  };
}

const verification = (status: string, sha256: string | null) => ({
  status,
  sha256,
  verifiedAt: '2026-10-11T09:00:00Z',
});

describe('Admin › Defensibility Reports (E14-T05)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(permissions: string[]) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Auditor', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/defensibility/review-summary`, {
        body: {
          statement: statement('ReviewSummary'),
          snapshot: null,
          population: {
            documents: 3,
            reviewed: 2,
            notReviewed: 1,
            systemCodedOnly: 0,
            bulkSkips: 0,
          },
          reviewers: [
            {
              userId: 'u-7',
              displayName: 'Rae Reviewer',
              documents: 2,
              changes: 5,
              interactive: 4,
              bulk: 1,
              systemRule: 0,
              firstAt: '2026-10-01T10:00:00Z',
              lastAt: '2026-10-02T10:00:00Z',
            },
          ],
          days: [
            {
              date: '2026-10-01',
              userId: 'u-7',
              displayName: 'Rae Reviewer',
              documents: 2,
              changes: 5,
              interactive: 4,
              bulk: 1,
              systemRule: 0,
            },
          ],
          daysTruncated: false,
        },
      })
      .on('GET', `${WS}/defensibility/search-term-hits`, {
        body: {
          statement: statement('SearchTermHits'),
          truncated: false,
          reports: [
            {
              reportId: 'r-1',
              name: 'Meet and confer terms',
              run: 1,
              scope: 'Workspace',
              snapshotId: '0199c3a0-0000-7000-8000-00000000c001',
              searchGeneration: 41,
              indexCurrent: false,
              indexPendingChanges: 3,
              indexLagSeconds: 4.5,
              executedBy: 'Alex Auditor',
              executedAt: '2026-10-05T10:00:00Z',
              completedAt: '2026-10-05T10:01:00Z',
              totals: {
                documentsInScope: 3,
                documentsWithHits: 2,
                documentsWithHitsIncludingFamily: 2,
                documentsWithoutHits: 1,
              },
              terms: [],
            },
          ],
        },
      })
      .on('GET', `${WS}/documents/${DOC}/chain-of-custody`, {
        body: {
          statement: statement('ChainOfCustody'),
          documentId: DOC,
          controlNumber: 'ACME0001',
          fileName: 'memo.docx',
          fileSize: 120,
          createdAt: '2026-09-01T10:00:00Z',
          importHashes: { sha256: HASH, sha1: null, md5: null, upstream: null },
          verified: 2,
          mismatches: 1,
          imports: [
            {
              importId: 'i-1',
              name: 'Vendor volume 1',
              mode: 'Append',
              loadFileName: 'VOL001.dat',
              loadFileSha256: 'b'.repeat(64),
              loadFileSize: 100,
              loadFileVerification: verification('Match', 'b'.repeat(64)),
              rowNumber: 1,
              action: 'Imported',
              startedBy: 'Alex Auditor',
              startedAt: '2026-09-01T09:00:00Z',
              completedAt: '2026-09-01T09:05:00Z',
              jobId: 'j-1',
            },
          ],
          objects: [
            {
              objectId: 'o-1',
              area: 'Native',
              current: true,
              pageSetId: null,
              page: null,
              sizeBytes: 120,
              contentType: 'application/octet-stream',
              storedAt: '2026-09-01T09:01:00Z',
              jobId: 'j-1',
              recordedSha256: HASH,
              verification: verification('Mismatch', 'c'.repeat(64)),
            },
          ],
          objectsTruncated: false,
          pageSets: [],
          overlays: [],
          productions: [],
        },
      });
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/admin/defensibility');
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
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).endsWith(label))!;
  const input = (label: string) => {
    const l = [...root().querySelectorAll('label')].find((x) => text(x).startsWith(label))!;
    return root().querySelector<HTMLInputElement>(`#${l.getAttribute('for')}`)!;
  };
  async function type(label: string, value: string): Promise<void> {
    const el = input(label);
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle();
  }

  it('generates reports only when asked and states their point and fingerprint', async () => {
    await setup([PERMISSIONS.complianceReport, PERMISSIONS.auditExport]);
    expect(text(root().querySelector('main h1')!)).toBe('Defensibility Reports');
    expect(api.urls().filter((u) => u.includes('/defensibility/'))).toEqual([]);

    button('Generate summary').click();
    await settle();
    expect(api.urls().find((u) => u.includes('/review-summary'))).toBe(
      `${WS}/defensibility/review-summary`,
    );
    expect(text()).toContain('f'.repeat(64));
    expect(text()).toContain('3 documents in scope');
    expect(text()).toContain('2 reviewed');
    expect(text(root().querySelector('table')!)).toContain('Rae Reviewer');

    button('Generate hit reports').click();
    await settle();
    expect(text()).toContain('Catching up');
    expect(text()).toContain('3 changes not yet searchable, oldest 4.5 s');

    const links = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toContain(`${WS}/defensibility/review-summary/export`);
    expect(links).toContain(`${WS}/defensibility/production-history/export`);
    expect(links).toContain(`${WS}/defensibility/pack`);
    expect(text()).toContain('It also holds the audit trail');
    await expectNoAxeViolations(root());
  });

  it('passes the report point to every report and download', async () => {
    await setup([PERMISSIONS.complianceReport]);
    await type('As of', '2026-10-10T12:00');
    const asOf = encodeURIComponent(localDateTime('2026-10-10T12:00')!);
    button('Generate summary').click();
    await settle();
    expect(api.urls().find((u) => u.includes('/review-summary'))).toBe(
      `${WS}/defensibility/review-summary?asOf=${asOf}`,
    );
    const pack = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].find((a) =>
      text(a).includes('report pack'),
    )!;
    expect(pack.getAttribute('href')).toBe(`${WS}/defensibility/pack?asOf=${asOf}`);
    expect(text()).not.toContain('It also holds the audit trail');

    await type('As of', '2999-01-01T00:00');
    expect(text()).toContain('The point cannot be in the future.');
    expect(button('Generate summary').disabled).toBe(true);
    expect(pack.hasAttribute('href')).toBe(false);
  });

  it('traces a document: import hashes next to the hashes read back from storage', async () => {
    await setup([PERMISSIONS.complianceReport]);
    // Submitting without a valid ID does nothing; the button stays enabled so Enter right after typing submits.
    button('Trace document').click();
    await settle();
    expect(text()).not.toContain('Files do not match');
    await type('Document ID', DOC);
    button('Trace document').click();
    await settle();
    expect(text()).toContain('Files do not match');
    const files = [...root().querySelectorAll('table')].find((t) =>
      text(t).includes('SHA-256 read back now'),
    )!;
    expect(text(files)).toContain('Does not match');
    expect(files.querySelector(`[title="${HASH}"]`)).not.toBeNull();
    expect(files.querySelector(`[title="${'c'.repeat(64)}"]`)).not.toBeNull();
    const download = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].find((a) =>
      text(a).includes('chain of custody'),
    )!;
    expect(download.getAttribute('href')).toBe(`${WS}/documents/${DOC}/chain-of-custody/export`);
    await expectNoAxeViolations(root());
  });

  it('validates the pack documents', async () => {
    await setup([PERMISSIONS.complianceReport]);
    await type('Documents to trace', `${DOC}, not-an-id`);
    expect(text(root().querySelector('[role="alert"]')!)).toBe(
      'Enter document IDs separated by commas or spaces.',
    );
    await type('Documents to trace', `${DOC} ${DOC.toUpperCase()}`);
    const pack = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].find((a) =>
      text(a).includes('report pack'),
    )!;
    expect(pack.getAttribute('href')).toBe(`${WS}/defensibility/pack?documentId=${DOC}`);
  });

  it('parses document IDs and local times', () => {
    expect(parseDocumentIds('')).toEqual([]);
    expect(parseDocumentIds(`${DOC}\n${DOC}`)).toEqual([DOC]);
    expect(parseDocumentIds('x')).toBeNull();
    expect(localDateTime('')).toBeNull();
    expect(localDateTime('nonsense')).toBeNull();
  });
});
