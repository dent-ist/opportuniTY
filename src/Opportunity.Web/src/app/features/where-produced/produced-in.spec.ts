import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { problemDetailsInterceptor } from '../../core/api/http';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { ProducedIn } from './produced-in';

const WS = '/api/v1/workspaces/ws-1';

function item(extra: Record<string, unknown>) {
  return {
    productionId: 'prod-1',
    productionName: 'First production',
    productionVersion: 1,
    productionStatus: 'finalized',
    producedAt: '2026-05-04T10:00:00Z',
    voidedAt: null,
    volumes: ['ACM_VOL001'],
    documentId: 'doc-1',
    controlNumber: 'ACM0000001',
    relations: ['self'],
    output: 'image',
    prodBegBates: 'ACM0000001',
    prodEndBates: 'ACM0000003',
    prodBegAttach: 'ACM0000001',
    prodEndAttach: 'ACM0000004',
    ...extra,
  };
}

const LOOKUP = {
  documentId: 'doc-1',
  documents: [],
  truncated: false,
  items: [
    item({}),
    item({
      documentId: 'doc-2',
      controlNumber: 'ACM0000002',
      relations: ['family'],
      prodBegBates: 'ACM0000004',
      prodEndBates: 'ACM0000004',
    }),
    item({
      productionId: 'prod-2',
      productionName: 'Second production',
      productionStatus: 'voided',
      voidedAt: '2026-06-01T08:00:00Z',
      volumes: [],
      documentId: 'doc-4',
      controlNumber: 'ACM0000004',
      relations: ['duplicate'],
      output: 'native',
      prodBegBates: 'ACM0000100',
      prodEndBates: 'ACM0000100',
      prodBegAttach: 'ACM0000100',
      prodEndAttach: 'ACM0000100',
    }),
  ],
};

describe('Produced in (E13-T04)', () => {
  let api: FakeApi;

  function create(documentId: string) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([problemDetailsInterceptor])),
        provideFakeApi(api),
        {
          provide: WorkspaceContext,
          useValue: {
            workspaceId: 'ws-1',
            workspace: { displayTimeZone: 'UTC' },
            apiUrl: (...s: string[]) => [WS, ...s].join('/'),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ProducedIn);
    fixture.componentRef.setInput('documentId', documentId);
    fixture.componentRef.setInput('controlNumber', 'ACM0000001');
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await fixture.whenStable();
    }
  }

  const rows = (el: HTMLElement) =>
    [...el.querySelectorAll('tbody tr')].map((r) =>
      [...r.querySelectorAll('td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim()),
    );

  beforeEach(() => {
    api = new FakeApi()
      .on('GET', `${WS}/documents/doc-1/where-produced`, { body: LOOKUP })
      .on('GET', `${WS}/documents/doc-9/where-produced`, {
        body: { documentId: 'doc-9', documents: [], items: [], truncated: false },
      });
  });

  it('lists every production, volume and Bates range of the document, its family and duplicates', async () => {
    const fixture = create('doc-1');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Produced in 2 productions (3 Bates ranges).');
    const table = rows(el);
    expect(table.map((r) => [r[0], r[2], r[3], r[4], r[5], r[6], r[7]])).toEqual([
      [
        'First production (v1)',
        'ACM_VOL001',
        'This document',
        'ACM0000001',
        'ACM0000001 – ACM0000003',
        'ACM0000001 – ACM0000004',
        'Images',
      ],
      [
        'First production (v1)',
        'ACM_VOL001',
        'Family member',
        'ACM0000002',
        'ACM0000004',
        'ACM0000001 – ACM0000004',
        'Images',
      ],
      [
        'Second production (v1)',
        'No volume written yet',
        'Duplicate',
        'ACM0000004',
        'ACM0000100',
        'ACM0000100',
        'Native',
      ],
    ]);
    expect(table[0][1]).toMatch(/^Produced /);
    expect(table[2][1]).toMatch(/^Voided /);

    const link = el.querySelector<HTMLAnchorElement>('a[download]')!;
    expect(link.getAttribute('href')).toBe(
      `${WS}/productions/bates-cross-reference?documentId=doc-1`,
    );
    expect(link.textContent).toContain('Download cross-reference (CSV)');

    const opened: unknown[] = [];
    fixture.componentInstance.openDocument.subscribe((d) => opened.push(d));
    const open = [...el.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.trim() === 'ACM0000002',
    )!;
    open.click();
    expect(opened).toEqual([{ documentId: 'doc-2', controlNumber: 'ACM0000002' }]);
    expect(el.querySelectorAll('tbody button')).toHaveLength(2);

    await expectNoAxeViolations(el);
  });

  it('says so when nothing related was produced', async () => {
    const fixture = create('doc-9');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('table')).toBeNull();
    expect(el.textContent?.replace(/\s+/g, ' ')).toContain(
      'were produced in a finalized production',
    );
    expect(el.querySelector('a[download]')).toBeNull();
  });

  it('reports a lookup that failed', async () => {
    const fixture = create('doc-404');
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('could not be looked up');
  });
});
