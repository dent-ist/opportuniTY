import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, Injectable, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  DocumentPageResource,
  DocumentResource,
  SearchHit,
} from '../../../../core/api/generated/models';
import { CommandRegistry } from '../../../../core/commands';
import { PreferenceStorage } from '../../../../core/preferences/preference-storage';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import { expectNoAxeViolations } from '../../../../ui/testing/axe.testing';
import { hit } from '../../grid/grid-fixtures.testing';
import type { LoadedDocument } from '../document-loader';
import {
  ContentPurpose,
  Delivered,
  DocumentContentApi,
  PageImageKind,
  TextChunk,
  toTextChunk,
} from '../review-ports';
import { DocumentViewer, ViewerDocument } from './document-viewer';
import { documentResource, pageResource, textChunkResource } from './viewer-fixtures.testing';
import { initialMode, modeAvailability } from './viewer-modes';

/** The content port, answered in memory; every call is logged as `kind:documentId[:detail]:purpose`. */
@Injectable()
class FakeContentApi extends DocumentContentApi {
  readonly calls: string[] = [];
  readonly downloads: string[] = [];
  chunks: Record<number, TextChunk[]> = {};
  pageList: DocumentPageResource[] = [];

  async document(id: string, purpose: ContentPurpose): Promise<Delivered<DocumentResource>> {
    this.calls.push(`metadata:${id}:${purpose}`);
    return { value: documentResource(Number(id.slice(4))), retrievalId: 'm' };
  }
  async textChunk(id: string, index: number, purpose: ContentPurpose): Promise<TextChunk> {
    this.calls.push(`text:${id}:${index}:${purpose}`);
    const chunk = this.chunks[Number(id.slice(4))]?.[index];
    if (!chunk) throw new Error('no chunk');
    return chunk;
  }
  async pages(id: string, purpose: ContentPurpose) {
    this.calls.push(`pages:${id}:${purpose}`);
    return { value: this.pageList, retrievalId: 'p' };
  }
  async pageImage(id: string, page: number, kind: PageImageKind, purpose: ContentPurpose) {
    this.calls.push(`${kind}:${id}:${page}:${purpose}`);
    return { value: new Blob([`page ${page}`]), retrievalId: `i-${page}` };
  }
  downloadNative(id: string, fileName: string): void {
    this.downloads.push(`${id} as ${fileName}`);
  }
  async recordView(): Promise<void> {
    this.calls.push('view');
  }
}

@Component({
  selector: 'opp-test-host',
  imports: [DocumentViewer],
  template: `<section aria-label="Viewer" data-command-scope="viewer">
    <opp-document-viewer [document]="doc()" />
  </section>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly doc = signal<ViewerDocument>({ hit: hit(1), state: 'loading', content: null });
}

/** Chunks of `text` split every `size` characters, as the gateway would deliver them. */
function chunksOf(n: number, text: string, size: number): TextChunk[] {
  const parts = text.match(new RegExp(`[\\s\\S]{1,${size}}`, 'g')) ?? [''];
  return parts.map((part, index) =>
    toTextChunk(
      `doc-${n}`,
      textChunkResource(part, {
        chunkIndex: index,
        chunkCount: parts.length,
        isLast: index === parts.length - 1,
      }),
      `r-${index}`,
    ),
  );
}

function loaded(n: number, resource: DocumentResource, extra: Partial<LoadedDocument> = {}) {
  const availability = modeAvailability(resource);
  return {
    documentId: `doc-${n}`,
    metadata: resource,
    availability,
    mode: initialMode(availability, null),
    text: null,
    pages: null,
    firstImage: null,
    retrievalId: 'm',
    ...extra,
  } satisfies LoadedDocument;
}

describe('Document viewer (E16-T04)', () => {
  let api: FakeContentApi;
  let host: Host;
  let root: HTMLElement;
  let announced: () => string[];
  let canDownload: boolean;

  async function setup(permissions: { download?: boolean } = {}): Promise<void> {
    canDownload = permissions.download ?? true;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DocumentContentApi, useClass: FakeContentApi },
        { provide: WorkspaceContext, useValue: { can: () => canDownload } },
      ],
    });
    localStorage.clear();
    const spy = vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    announced = () => spy.mock.calls.map((c) => String(c[0]));
    api = TestBed.inject(DocumentContentApi) as FakeContentApi;
    const fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle();
  }

  async function show(doc: ViewerDocument): Promise<void> {
    host.doc.set(doc);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      TestBed.tick();
    }
  }

  const tabs = () => [...root.querySelectorAll<HTMLElement>('[role="tab"]')];
  const tab = (name: string) => tabs().find((t) => t.textContent?.trim() === name)!;
  const selected = () => tabs().find((t) => t.getAttribute('aria-selected') === 'true');
  const description = (el: HTMLElement) =>
    document.getElementById(el.getAttribute('aria-describedby') ?? '')?.textContent;
  const button = (name: string) =>
    [...root.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name,
    )!;
  const invoke = (command: string) => TestBed.inject(CommandRegistry).invoke(command);

  afterEach(() => root?.remove());

  it('shows the modes in their fixed order; modes without an artifact are disabled with a reason', async () => {
    await setup();
    const resource = documentResource(5, {
      images: { available: false, status: 'pending' },
      native: { available: false },
    });
    await show({ hit: hit(5), state: 'ready', content: loaded(5, resource) });

    expect(tabs().map((t) => t.textContent?.trim())).toEqual([
      'Extracted Text',
      'Image',
      'Native',
      'Production',
      'Metadata',
    ]);
    const disabled = tabs().filter((t) => t.getAttribute('aria-disabled') === 'true');
    expect(disabled.map((t) => `${t.textContent?.trim()}: ${description(t)}`)).toEqual([
      'Image: Image rendering in progress',
      'Native: No native',
      'Production: Not produced',
    ]);
    expect(selected()?.textContent?.trim()).toBe('Extracted Text');
    await expectNoAxeViolations(root);
  }, 30_000);

  it('names a document without extracted text, opens it in another mode and offers the rest', async () => {
    await setup();
    TestBed.inject(PreferenceStorage).write('viewer.mode', { mode: 'text' });
    const resource = documentResource(7, { text: { available: false, chunkCount: 0 } });
    await show({ hit: hit(7), state: 'ready', content: loaded(7, resource, { mode: 'metadata' }) });

    expect(selected()?.textContent?.trim()).toBe('Metadata');
    expect(root.querySelector('.viewer__fallback')?.textContent).toContain(
      'No extracted text for this document. Showing Metadata.',
    );
    expect(description(tab('Extracted Text'))).toBe('No extracted text for this document');
    expect(root.textContent).not.toContain('cannot be shown');

    // The offered mode switches and becomes the reviewer's mode; the notice goes.
    button('Show Native').click();
    await settle();
    expect(selected()?.textContent?.trim()).toBe('Native');
    expect(TestBed.inject(PreferenceStorage).read('viewer.mode')).toEqual({ mode: 'native' });
    expect(root.querySelector('.viewer__fallback')).toBeNull();
  });

  it('switches modes with the keyboard commands, remembers the last mode and explains unavailable ones', async () => {
    await setup();
    api.chunks[2] = chunksOf(2, 'Hello from document 2', 1000);
    await show({
      hit: hit(2),
      state: 'ready',
      content: loaded(2, documentResource(2), { mode: 'text', text: api.chunks[2][0] }),
    });
    expect(api.calls).toEqual([]); // chunk 0 came with the document

    invoke('viewer.mode.metadata');
    await settle();
    expect(selected()?.textContent?.trim()).toBe('Metadata');
    expect(TestBed.inject(PreferenceStorage).read('viewer.mode')).toEqual({ mode: 'metadata' });

    invoke('viewer.mode.image');
    await settle();
    expect(selected()?.textContent?.trim()).toBe('Metadata');
    expect(announced()).toContain('Image is not available: No images.');

    // The next document opens in the remembered mode.
    await show({ hit: hit(3), state: 'ready', content: loaded(3, documentResource(3)) });
    expect(selected()?.textContent?.trim()).toBe('Metadata');
  });

  it('streams long text in chunks, finds in the loaded text and searches the whole document on request', async () => {
    await setup();
    const line = (i: number) => `Line ${i} of the agreement.\n`;
    const text = Array.from({ length: 300 }, (_, i) => line(i)).join('') + 'final notice\n';
    api.chunks[4] = chunksOf(4, text, 2048);
    const count = api.chunks[4].length;
    await show({ hit: hit(4), state: 'ready', content: loaded(4, documentResource(4)) });

    expect(api.calls).toEqual(['text:doc-4:0:display']);
    const scroller = () =>
      root.querySelector<HTMLElement>('[aria-label="Extracted text of ACM0000004"]')!;
    expect(scroller().textContent).toContain('Line 0 of the agreement.');
    expect(scroller().textContent).not.toContain('final notice');
    // Lines are never split where a chunk ends.
    const blocks = [...scroller().querySelectorAll('[data-segment]')].map((b) => b.textContent!);
    expect(blocks.slice(0, -1).every((b) => b.endsWith('\n'))).toBe(true);

    button('Show more text').click();
    await settle();
    expect(api.calls).toEqual(['text:doc-4:0:display', 'text:doc-4:1:display']);

    const find = root.querySelector<HTMLInputElement>('input[aria-label="Find in document"]')!;
    find.value = 'final notice';
    find.dispatchEvent(new Event('input'));
    await settle();
    expect(root.querySelector('#viewer-find-count')?.textContent).toBe('No matches in loaded text');

    button('Search whole document').click();
    await settle();
    await settle();
    expect(api.calls).toHaveLength(count);
    expect(scroller().textContent).toContain('final notice');
    expect(root.querySelector('#viewer-find-count')?.textContent).toBe('1 of 1');
    expect(announced()).toContain('Match 1 of 1');
  });

  it('highlights the search hit terms and steps through them (hook for Highlight Sets, E16-T12)', async () => {
    await setup();
    api.chunks[6] = chunksOf(6, 'The agreement ends. Agreement terms. Agreements differ.', 1000);
    const withHits = hit(6, {
      snippets: [{ text: 'the agreement ends', highlights: [{ start: 4, end: 13 }] }],
    });
    await show({ hit: withHits, state: 'ready', content: loaded(6, documentResource(6)) });

    // Whole words only: "Agreements" is not a hit.
    expect(root.textContent).toContain('2 search hits');
    invoke('viewer.nextHit');
    await settle();
    expect(root.textContent).toContain('Hit 1 of 2');
    expect(announced()).toContain("Hit 1 of 2 'agreement'");
    invoke('viewer.toggleHighlights');
    await settle();
    expect(root.querySelector<HTMLInputElement>('opp-checkbox input')!.checked).toBe(false);
  });

  it('shows page images with page navigation, go to page, zoom, fit and rotate; image URLs do not outlive the page', async () => {
    let urls = 0;
    const created: string[] = [];
    URL.createObjectURL = vi.fn(() => {
      const url = `blob:test/${++urls}`;
      created.push(url);
      return url;
    });
    URL.revokeObjectURL = vi.fn();
    await setup();
    api.pageList = [1, 2, 3].map(pageResource);
    const resource = documentResource(14, {
      images: { available: true, pageCount: 3, source: 'imported', status: 'ready' },
    });
    await show({
      hit: hit(14),
      state: 'ready',
      content: loaded(14, resource, {
        mode: 'image',
        pages: api.pageList,
        firstImage: { pageNumber: 1, blob: new Blob(['page 1']) },
      }),
    });
    expect(selected()?.textContent?.trim()).toBe('Image');
    const page = () => root.querySelector<HTMLImageElement>('img.image__page');
    expect(page()?.alt).toBe('Page 1 of ACM0000014');
    // Page 1 came with the document; page 2 is fetched ahead as prefetch.
    expect(api.calls).toEqual(['image:doc-14:2:prefetch']);

    invoke('viewer.nextPage');
    await settle();
    expect(page()?.alt).toBe('Page 2 of ACM0000014');
    expect(api.calls).toEqual(['image:doc-14:2:prefetch', 'image:doc-14:3:prefetch']);
    expect(root.querySelector('.thumb[aria-current="page"]')?.getAttribute('aria-label')).toBe(
      'Page 2',
    );

    const goTo = root.querySelector<HTMLInputElement>('.image__page-input')!;
    goTo.value = '3';
    goTo.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();
    expect(page()?.alt).toBe('Page 3 of ACM0000014');
    goTo.value = '9';
    goTo.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();
    expect(announced()).toContain('There is no page 9. Pages 1 to 3.');

    button('Zoom in').click(); // from fit width (the stage has no size in jsdom: 100 %)
    await settle();
    expect(root.querySelector('.viewer-tools__value')?.textContent).toBe('125%');
    for (let i = 0; i < 12; i++) invoke('viewer.zoomIn');
    await settle();
    expect(root.querySelector('.viewer-tools__value')?.textContent).toBe('400%');
    for (let i = 0; i < 12; i++) invoke('viewer.zoomOut');
    await settle();
    expect(root.querySelector('.viewer-tools__value')?.textContent).toBe('25%');
    invoke('viewer.rotate');
    await settle();
    expect(page()?.style.transform).toContain('rotate(90deg)');
    // The frame is the rotated bounding box.
    const frame = root.querySelector<HTMLElement>('.image__frame')!;
    expect(parseFloat(frame.style.width)).toBeGreaterThan(parseFloat(frame.style.height));
    await expectNoAxeViolations(root);

    // Leaving the document revokes every image URL it created.
    await show({ hit: hit(2), state: 'ready', content: loaded(2, documentResource(2)) });
    expect(new Set(vi.mocked(URL.revokeObjectURL).mock.calls.map((c) => c[0]))).toEqual(
      new Set(created),
    );
  }, 30_000);

  it('offers Download native only with Document.DownloadNative and never renders the native', async () => {
    await setup();
    TestBed.inject(PreferenceStorage).write('viewer.mode', { mode: 'native' });
    await show({ hit: hit(2), state: 'ready', content: loaded(2, documentResource(2)) });
    expect(selected()?.textContent?.trim()).toBe('Native');
    expect(root.querySelector('iframe, object, embed')).toBeNull();
    button('Download native').click();
    expect(api.downloads).toEqual(['doc-2 as ACM0000002.msg']);
    expect(root.textContent).toContain('2 KB');
    root.remove();
    TestBed.resetTestingModule();

    await setup({ download: false });
    TestBed.inject(PreferenceStorage).write('viewer.mode', { mode: 'native' });
    await show({ hit: hit(2), state: 'ready', content: loaded(2, documentResource(2)) });
    expect(button('Download native')).toBeUndefined();
    expect(root.textContent).toContain('Your role does not allow downloading natives');
  });

  it('lists the fields with display values and the imported value in a tooltip', async () => {
    await setup();
    TestBed.inject(PreferenceStorage).write('viewer.mode', { mode: 'metadata' });
    await show({ hit: hit(2), state: 'ready', content: loaded(2, documentResource(2)) });
    const rows = [...root.querySelectorAll('tbody tr')].map((r) =>
      [...r.children].map((c) => c.textContent?.trim()),
    );
    expect(rows).toEqual([
      ['Control Number', 'ACM0000002', 'Text'],
      ['Document Date', '2024-03-01 14:30 UTC', 'Text'],
      ['Custodian', 'Smith, Jordan', 'Text'],
    ]);
    const date = root.querySelector<HTMLElement>('.metadata__value--raw')!;
    expect(description(date)).toBe('Imported value: 3/1/2024 2:30 PM');
    const filter = root.querySelector<HTMLInputElement>('input[aria-label="Filter fields"]')!;
    filter.value = 'cust';
    filter.dispatchEvent(new Event('input'));
    await settle();
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(root.textContent).toContain('1 of 3 fields');
    await expectNoAxeViolations(root);
  }, 30_000);
});
