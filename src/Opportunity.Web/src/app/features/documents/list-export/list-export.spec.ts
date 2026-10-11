import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../../core/api/fake-api.testing';
import type { ListExportResource, SearchRequest } from '../../../core/api/generated/models';
import { provideOpportunityHttp } from '../../../core/api/http';
import { CommandRegistry } from '../../../core/commands';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { fakePage } from '../grid/grid-fixtures.testing';
import {
  LIST_EXPORT_POLL_MS,
  exportColumns,
  progressText,
  rowsProblem,
  sizeText,
  toRequest,
} from './list-export-model';

const WS = '/api/v1/workspaces/ws-1';

function field(id: number, queryName: string, displayName: string) {
  return {
    fieldId: id,
    queryName,
    displayName,
    type: 'text',
    storage: 'column',
    multiValue: false,
    isSystem: true,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    reducedCapabilities: false,
    capabilities: { sortable: true, filterable: true, leadingWildcard: false },
    choices: null,
  };
}

const FIELDS = [
  field(1, 'controlnumber', 'Control Number'),
  field(6, 'filename', 'File Name'),
  field(16, 'date', 'Document Date'),
];

function exportResource(
  status: string,
  extra: Partial<ListExportResource> = {},
): ListExportResource {
  return {
    exportId: 'lx-1',
    jobId: 'job-1',
    name: 'Export List',
    status: status as ListExportResource['status'],
    statusReason: null,
    snapshotId: 'snap-1',
    documentCount: 250,
    columns: [],
    createdAt: '2026-10-11T10:42:00Z',
    completedAt: null,
    rowCount: null,
    documentsExcluded: null,
    byteCount: null,
    contentSha256: null,
    ...extra,
  };
}

describe('Export List (CSV) (#181)', () => {
  describe('model', () => {
    it('maps the view to field ids in order, without Control Number or unknown fields', () => {
      expect(
        exportColumns(FIELDS as never, ['filename', 'controlnumber', 'nosuch', 'DATE']),
      ).toEqual([
        { fieldId: 6, label: 'File Name' },
        { fieldId: 16, label: 'Document Date' },
      ]);
      expect(exportColumns(null, ['filename'])).toEqual([]);
    });

    it('sends the whole list as its query and checked rows as ids', () => {
      const columns = [{ fieldId: 6, label: 'File Name' }];
      expect(
        toRequest(
          {
            kind: 'all',
            query: 'memo',
            generation: null,
            total: { value: 3, relation: 'eq' },
            countText: '3',
            expand: { family: true },
          },
          columns,
        ),
      ).toEqual({ fieldIds: [6], query: 'memo', expand: { family: true } });
      expect(toRequest({ kind: 'documents', documentIds: ['d1'] }, columns)).toEqual({
        fieldIds: [6],
        documentIds: ['d1'],
      });
      expect(rowsProblem(null)).toContain('no documents');
      expect(rowsProblem({ kind: 'documents', documentIds: Array(1001).fill('d') })).toContain(
        'More than 1,000',
      );
    });

    it('describes progress, exclusions and size', () => {
      expect(progressText(exportResource('queued', { documentCount: null }), 'en')).toBe(
        'Freezing the list…',
      );
      expect(progressText(exportResource('running'), 'en')).toBe('Writing 250 rows…');
      expect(
        progressText(exportResource('completed', { rowCount: 249, documentsExcluded: 1 }), 'en'),
      ).toBe('Ready: 249 rows. 1 document was left out because you can no longer access it.');
      expect(
        progressText(exportResource('failed', { statusReason: 'Permission lost.' }), 'en'),
      ).toBe('The export failed: Permission lost.');
      expect(sizeText(512, 'en')).toBe('512 B');
      expect(sizeText(12_700, 'en')).toBe('12.4 KB');
    });
  });

  describe('in the Documents list', () => {
    let api: FakeApi;
    let harness: RouterTestingHarness;
    let reads: number;

    async function setup(permissions: string[], create?: () => FakeResponse): Promise<void> {
      reads = 0;
      api = new FakeApi()
        .on('GET', '/api/v1/me', {
          body: { userId: 'u-1', displayName: 'Pat Manager', email: 'p@example.test', groups: [] },
        })
        .on('GET', WS, {
          body: {
            workspaceId: 'ws-1',
            name: 'Acme v. Widget',
            displayTimeZone: 'UTC',
            permissions,
          },
        })
        .on('GET', `${WS}/fields`, {
          body: { items: FIELDS, nextCursor: null, total: { value: 3, relation: 'eq' } },
        })
        .on('POST', `${WS}/searches`, (req) => {
          const query = (req.body as SearchRequest).query ?? '';
          return { body: fakePage({ total: query ? 12 : 250, pageSize: 100 }, 1) };
        })
        .on(
          'POST',
          `${WS}/list-exports`,
          create ?? (() => ({ status: 202, body: exportResource('queued') })),
        )
        .on('GET', `${WS}/list-exports/lx-1`, () => {
          reads++;
          return {
            body:
              reads === 1
                ? exportResource('running')
                : exportResource('completed', {
                    rowCount: 249,
                    documentsExcluded: 1,
                    byteCount: 12_700,
                  }),
          };
        });
      TestBed.configureTestingModule({
        providers: [
          ...provideAppRouting(),
          ...provideOpportunityHttp(),
          ...provideFakeApi(api),
          { provide: LIST_EXPORT_POLL_MS, useValue: 1 },
        ],
      });
      vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
      localStorage.clear();
      harness = await RouterTestingHarness.create();
      await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
      await TestBed.inject(CommandRegistry).ready();
      await settle();
    }

    async function settle(ms = 0): Promise<void> {
      for (let i = 0; i < 4; i++) {
        await new Promise((resolve) => setTimeout(resolve, ms));
        await harness.fixture.whenStable();
      }
    }

    const root = () => harness.routeNativeElement as HTMLElement;
    const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
    const text = (el: Element | null = root()) => el?.textContent?.replace(/\s+/g, ' ') ?? '';
    const buttonIn = (el: Element | Document, name: string) =>
      [...el.querySelectorAll<HTMLButtonElement>('button')].find(
        (b) => b.textContent?.replace(/\s+/g, ' ').trim() === name,
      );

    async function openExportList(menuOpen = false): Promise<void> {
      if (!menuOpen) {
        buttonIn(root(), 'Mass Actions')!.click();
        await settle();
      }
      buttonIn(document, 'Export List (CSV)…')!.click();
      for (let i = 0; i < 200 && !dialog(); i++) await settle(10);
      await settle();
    }

    afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

    it('exports the whole list with the view columns, follows the job and offers the gateway download', async () => {
      await setup([PERMISSIONS.documentView, PERMISSIONS.searchExecute, 'Document.ExportList']);
      // Mass Edit needs Coding.Bulk: only the export is offered.
      buttonIn(root(), 'Mass Actions')!.click();
      await settle();
      expect(buttonIn(document, 'Mass Edit…')).toBeUndefined();

      await openExportList(true);
      const d = dialog()!;
      expect(text(d)).toContain('All results of the list (250)');
      expect(text(d)).toContain('Control Number');
      expect(text(d)).toContain('Document Date');
      expect(text(d)).toContain('File Name');
      await expectNoAxeViolations(d);

      buttonIn(d, 'Export')!.click();
      await settle(20);
      const [create] = api.requests.filter(
        (r) => r.method === 'POST' && r.url === `${WS}/list-exports`,
      );
      expect(create.body).toEqual({ fieldIds: [16, 6], query: '' });
      expect(create.headers.get('Idempotency-Key')).toBeTruthy();
      for (let i = 0; i < 50 && !text(dialog()).includes('Ready'); i++) await settle(5);
      expect(text(dialog())).toContain('Ready: 249 rows. 1 document was left out');
      const link = dialog()!.querySelector<HTMLAnchorElement>('a[download]')!;
      expect(link.getAttribute('href')).toBe(`${WS}/list-exports/lx-1/content`);
      expect(text(link)).toContain('Download CSV (12.4 KB)');
      await expectNoAxeViolations(dialog()!);
    });

    it('explains a refusal from the server and hides the action without the permission', async () => {
      await setup(
        [PERMISSIONS.documentView, PERMISSIONS.searchExecute, 'Document.ExportList'],
        () => ({
          status: 422,
          body: {
            title: 'Unprocessable',
            status: 422,
            code: 'unprocessable-content',
            detail:
              'The list has 2,000,000 documents, more than one export may hold; narrow it with a filter.',
          },
        }),
      );
      await openExportList();
      buttonIn(dialog()!, 'Export')!.click();
      await settle(20);
      expect(dialog()!.querySelector('[role="alert"]')?.textContent).toContain(
        'narrow it with a filter',
      );

      document.querySelector('.cdk-overlay-container')?.replaceChildren();
      TestBed.resetTestingModule();
      await setup([PERMISSIONS.documentView, PERMISSIONS.searchExecute]);
      expect(buttonIn(root(), 'Mass Actions')).toBeUndefined();
    });
  });
});
