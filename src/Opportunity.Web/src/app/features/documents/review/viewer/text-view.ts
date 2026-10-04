import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { CommandRegistry } from '../../../../core/commands';
import { ApiError, toApiError } from '../../../../core/api/problem-details';
import {
  Announcer,
  Button,
  Checkbox,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
} from '../../../../ui';
import { DocumentContentApi, TextChunk } from '../review-ports';
import type { ViewerModeDefinition } from './viewer-modes';
import {
  TextSegment,
  TextSegments,
  findPattern,
  matchOffsets,
  termsPattern,
} from './text-segments';

/** Matches highlighted per block; more are counted only up to this many (a pathological find). */
const MATCHES_PER_SEGMENT = 1000;

/** Highlight names of the CSS Custom Highlight API (styled in text-view.scss with `::highlight()`). */
const HIGHLIGHT = { find: 'opp-find', current: 'opp-find-current', hits: 'opp-hit' } as const;

/**
 * Extracted Text mode (E16-T04, familiarity guide §3.2): the document's text streamed in 256 KiB chunks through the
 * gateway (`…/text/chunks/{n}`, purpose display). The first chunk is on screen as soon as it arrives; the next one
 * loads when the reader nears the end of what is loaded, so a 10 MB text never loads or lays out at once. Text is
 * rendered in blocks that the browser lays out only near the viewport (`content-visibility: auto`).
 *
 * - Find in document (Ctrl/⌘+F inside the viewer): matches in the loaded text, "Search whole document" loads
 *   the rest first. Enter / Shift+Enter (or F3 / Shift+F3) step through matches.
 * - Search hits: the hit's highlighted terms (snippet highlights) are highlighted with colour and underline and
 *   stepped through with F3 / n; Alt+Shift+H toggles them. `terms` is the hook for persistent Highlight Sets
 *   (E16-T12, #138), which will pass their terms the same way.
 * - Banners for text truncated for search (Q-29) and for characters that could not be decoded.
 */
@Component({
  selector: 'opp-viewer-text',
  imports: [Button, Checkbox, EmptyState, ErrorState, Icon, IconButton, LoadingState],
  templateUrl: './text-view.html',
  styleUrls: ['./viewer-shared.scss', './text-view.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'viewer-mode' },
})
export class ViewerText {
  readonly documentId = input.required<string>();
  readonly controlNumber = input.required<string>();
  /** Chunk 0 when the document loader already has it (no second request). */
  readonly initial = input<TextChunk | null>(null);
  /** Terms to highlight: the search hit's terms now, Highlight Sets later (E16-T12). */
  readonly terms = input<readonly string[]>([]);
  /** The other modes the document has, offered when it turns out to have no text. */
  readonly otherModes = input<readonly ViewerModeDefinition[]>([]);
  readonly modeRequest = output<ViewerModeDefinition['mode']>();

  private readonly api = inject(DocumentContentApi);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private readonly findInput = viewChild<ElementRef<HTMLInputElement>>('findInput');

  protected readonly state = signal<'loading' | 'ready' | 'missing' | 'error'>('loading');
  protected readonly error = signal<ApiError | null>(null);
  private readonly chunks = signal<readonly TextChunk[]>([]);
  protected readonly segments = signal<readonly TextSegment[]>([]);
  /** Loading the next chunk: idle, on its way, or failed (a retry button is shown). */
  protected readonly more = signal<'idle' | 'loading' | 'error'>('idle');
  /** "Search whole document" progress: chunks loaded of all, while it runs. */
  protected readonly loadingAll = signal<{ loaded: number; total: number } | null>(null);

  private readonly firstChunk = computed(() => this.chunks()[0] ?? null);
  protected readonly complete = computed(() => this.chunks().at(-1)?.isLast ?? false);
  protected readonly truncated = computed(() => this.firstChunk()?.truncated ?? false);
  protected readonly encodingWarning = computed(() => this.firstChunk()?.encodingWarning ?? false);
  protected readonly progressText = computed(() => {
    const last = this.chunks().at(-1);
    return last ? `Part ${last.index + 1} of ${last.count} loaded` : '';
  });

  protected readonly query = signal('');
  protected readonly findCount = signal(0);
  protected readonly findIndex = signal(-1);
  protected readonly findLabel = computed(() => {
    if (!this.query().trim()) return '';
    const count = this.findCount();
    const scope = this.complete() ? '' : ' in loaded text';
    if (count === 0) return `No matches${scope}`;
    return this.findIndex() >= 0
      ? `${this.findIndex() + 1} of ${count}${scope}`
      : `${count} ${count === 1 ? 'match' : 'matches'}${scope}`;
  });

  protected readonly showHits = signal(true);
  protected readonly hitCount = signal(0);
  protected readonly hitIndex = signal(-1);
  private readonly hitPattern = computed(() => termsPattern(this.terms()));
  protected readonly hasTerms = computed(() => this.hitPattern() !== null);
  protected readonly hitLabel = computed(() => {
    const count = this.hitCount();
    const scope = this.complete() ? '' : ' in loaded text';
    if (count === 0) return `No search hits${scope}`;
    return this.hitIndex() >= 0
      ? `Hit ${this.hitIndex() + 1} of ${count}${scope}`
      : `${count} search ${count === 1 ? 'hit' : 'hits'}${scope}`;
  });

  private segmenter = new TextSegments();
  private readonly findRanges = new RangeIndex();
  private readonly hitRanges = new RangeIndex();
  /** Bumped when another document is shown; late chunk responses for an older one are dropped. */
  private seq = 0;
  private observer?: IntersectionObserver;

  constructor() {
    effect(() => {
      const documentId = this.documentId();
      const initial = this.initial();
      untracked(() => this.start(documentId, initial));
    });

    // Highlights follow the loaded text, the find text, the terms and the hits toggle (after the DOM has them).
    effect(() => {
      this.segments();
      this.query();
      this.hitPattern();
      this.showHits();
      untracked(() => afterNextRender(() => this.updateHighlights(), { injector: this.injector }));
    });

    // Load the next chunk when the end of the loaded text comes near (re-observed after each load).
    effect(() => {
      const sentinel = this.sentinel()?.nativeElement;
      const root = this.scroller()?.nativeElement;
      this.observer?.disconnect();
      this.observer = undefined;
      if (!sentinel || !root || typeof IntersectionObserver === 'undefined') return;
      this.segments(); // re-observe after every append, so a sentinel still in view loads again
      this.observer = new IntersectionObserver(
        (entries) => {
          if (entries.some((e) => e.isIntersecting)) untracked(() => void this.loadNext());
        },
        { root, rootMargin: '0px 0px 150% 0px' },
      );
      this.observer.observe(sentinel);
    });

    const registry = inject(CommandRegistry);
    registry.handle('viewer.find', () => this.focusFind());
    registry.handle('viewer.nextHit', () => this.step(1), { enabled: () => this.canStep() });
    registry.handle('viewer.previousHit', () => this.step(-1), { enabled: () => this.canStep() });
    registry.handle('viewer.toggleHighlights', () => this.toggleHits(), {
      enabled: () => this.hasTerms(),
    });

    inject(DestroyRef).onDestroy(() => {
      this.seq++;
      this.observer?.disconnect();
      for (const name of Object.values(HIGHLIGHT)) highlightRegistry()?.delete(name);
    });
  }

  // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────

  private start(documentId: string, initial: TextChunk | null): void {
    const seq = ++this.seq;
    this.segmenter = new TextSegments();
    this.chunks.set([]);
    this.segments.set([]);
    this.more.set('idle');
    this.loadingAll.set(null);
    this.findIndex.set(-1);
    this.hitIndex.set(-1);
    if (initial && initial.documentId === documentId && initial.index === 0) {
      this.accept(initial);
      return;
    }
    this.state.set('loading');
    this.api.textChunk(documentId, 0, 'display').then(
      (chunk) => seq === this.seq && this.accept(chunk),
      (e) => {
        if (seq !== this.seq) return;
        this.error.set(toApiError(e));
        this.state.set('error');
      },
    );
  }

  protected retry(): void {
    this.start(this.documentId(), null);
  }

  private accept(chunk: TextChunk): void {
    if (chunk.missing) {
      this.state.set('missing');
      return;
    }
    this.chunks.update((list) => [...list, chunk]);
    this.segments.set(this.segmenter.append(chunk.text));
    this.state.set('ready');
  }

  /** Loads the chunk after the last loaded one; false when there is none or it failed. */
  protected async loadNext(): Promise<boolean> {
    const last = this.chunks().at(-1);
    if (!last || last.isLast || this.more() === 'loading') return false;
    const seq = this.seq;
    this.more.set('loading');
    try {
      const chunk = await this.api.textChunk(this.documentId(), last.index + 1, 'display');
      if (seq !== this.seq) return false;
      this.more.set('idle');
      this.accept(chunk);
      return true;
    } catch {
      if (seq === this.seq) this.more.set('error');
      return false;
    }
  }

  /** "Search whole document": loads every remaining chunk, one at a time, then shows the first match. */
  protected async loadAll(): Promise<void> {
    const seq = this.seq;
    while (!this.complete() && seq === this.seq) {
      const last = this.chunks().at(-1)!;
      this.loadingAll.set({ loaded: last.index + 1, total: last.count });
      if (!(await this.loadNext())) break;
      // Let the browser paint between chunks, so a long text never blocks the page.
      await new Promise((resolve) => setTimeout(resolve));
    }
    if (seq !== this.seq) return;
    this.loadingAll.set(null);
    afterNextRender(
      () => {
        this.updateHighlights();
        if (this.query().trim() && this.findCount() > 0) this.step(1);
      },
      { injector: this.injector },
    );
  }

  // ── Find and hits ────────────────────────────────────────────────────────────────────────────────────────

  protected focusFind(): void {
    const el = this.findInput()?.nativeElement;
    el?.focus();
    el?.select();
  }

  protected onFindInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.findIndex.set(-1);
  }

  protected onFindKey(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.stepFind(event.shiftKey ? -1 : 1);
    } else if (event.key === 'Escape' && this.query()) {
      // Clears the find; a second Escape (empty box) is left to Review mode (Back to list).
      event.preventDefault();
      event.stopPropagation();
      this.query.set('');
      (event.target as HTMLInputElement).value = '';
      this.findIndex.set(-1);
    }
  }

  private canStep(): boolean {
    return this.query().trim() ? this.findCount() > 0 : this.showHits() && this.hitCount() > 0;
  }

  /** F3 / Shift+F3 (n / Shift+N): the find matches while there is a find text, else the search hits. */
  private step(delta: 1 | -1): void {
    if (this.query().trim()) this.stepFind(delta);
    else this.stepHit(delta);
  }

  protected stepFind(delta: 1 | -1): void {
    const ranges = this.findRanges.ranges;
    if (!ranges.length) {
      if (this.query().trim()) this.announcer.announce(this.findLabel());
      return;
    }
    const index = wrap(this.findIndex() + delta, ranges.length, this.findIndex() < 0 && delta < 0);
    this.findIndex.set(index);
    this.paintCurrent(ranges[index]);
    this.reveal(ranges[index]);
    this.announcer.announce(`Match ${index + 1} of ${ranges.length}`);
  }

  protected stepHit(delta: 1 | -1): void {
    const ranges = this.hitRanges.ranges;
    if (!ranges.length || !this.showHits()) return;
    const index = wrap(this.hitIndex() + delta, ranges.length, this.hitIndex() < 0 && delta < 0);
    this.hitIndex.set(index);
    this.paintCurrent(ranges[index]);
    this.reveal(ranges[index]);
    const term = ranges[index].toString();
    this.announcer.announce(`Hit ${index + 1} of ${ranges.length} '${term}'`);
  }

  protected toggleHits(): void {
    this.showHits.update((v) => !v);
    this.announcer.announce(this.showHits() ? 'Highlighting on' : 'Highlighting off');
  }

  private updateHighlights(): void {
    const root = this.scroller()?.nativeElement;
    if (!root) return;
    const find = this.findRanges.update(root, findPattern(this.query()), MATCHES_PER_SEGMENT);
    const hits = this.hitRanges.update(
      root,
      this.showHits() ? this.hitPattern() : null,
      MATCHES_PER_SEGMENT,
    );
    this.findCount.set(find.length);
    this.hitCount.set(hits.length);
    if (this.findIndex() >= find.length) this.findIndex.set(-1);
    if (this.hitIndex() >= hits.length) this.hitIndex.set(-1);
    const registry = highlightRegistry();
    if (!registry) return;
    registry.set(HIGHLIGHT.find, new Highlight(...find));
    registry.set(HIGHLIGHT.hits, new Highlight(...hits));
    const current =
      this.query().trim() && this.findIndex() >= 0
        ? find[this.findIndex()]
        : this.hitIndex() >= 0
          ? hits[this.hitIndex()]
          : undefined;
    this.paintCurrent(current);
  }

  private paintCurrent(range: Range | undefined): void {
    const registry = highlightRegistry();
    if (!registry) return;
    if (range) registry.set(HIGHLIGHT.current, new Highlight(range));
    else registry.delete(HIGHLIGHT.current);
  }

  /** Scrolls the match to the middle of the text area. */
  private reveal(range: Range): void {
    const root = this.scroller()?.nativeElement;
    const block = range.startContainer.parentElement;
    if (!root || !block) return;
    block.scrollIntoView?.({ block: 'center' });
    requestAnimationFrame(() => {
      const target = range.getBoundingClientRect?.();
      if (!target) return;
      const box = root.getBoundingClientRect();
      root.scrollTop += target.top - box.top - box.height / 2;
    });
  }
}

function wrap(index: number, length: number, fromEnd: boolean): number {
  if (fromEnd) return length - 1;
  return ((index % length) + length) % length;
}

/** The CSS Custom Highlight API registry, where the browser has it (not in jsdom). */
function highlightRegistry(): HighlightRegistry | undefined {
  return typeof Highlight === 'undefined'
    ? undefined
    : (globalThis.CSS as { highlights?: HighlightRegistry } | undefined)?.highlights;
}

/**
 * Text ranges of a pattern's matches in the rendered blocks, kept per block so appending a chunk only scans the
 * new blocks.
 */
class RangeIndex {
  ranges: Range[] = [];
  private source: string | null = null;
  private readonly bySegment = new Map<string, Range[]>();

  update(root: HTMLElement, pattern: RegExp | null, perSegment: number): Range[] {
    const source = pattern ? `${pattern.source}/${pattern.flags}` : null;
    if (source !== this.source) {
      this.bySegment.clear();
      this.source = source;
    }
    if (!pattern) return (this.ranges = []);
    const seen = new Set<string>();
    const ranges: Range[] = [];
    for (const block of root.querySelectorAll<HTMLElement>('[data-segment]')) {
      const id = block.dataset['segment']!;
      seen.add(id);
      let found = this.bySegment.get(id);
      if (!found) {
        const node = block.firstChild;
        found =
          node && node.nodeType === Node.TEXT_NODE
            ? matchOffsets((node as Text).data, pattern, perSegment).map(({ start, end }) => {
                const range = block.ownerDocument.createRange();
                range.setStart(node, start);
                range.setEnd(node, end);
                return range;
              })
            : [];
        this.bySegment.set(id, found);
      }
      for (const range of found) ranges.push(range);
    }
    for (const id of [...this.bySegment.keys()]) if (!seen.has(id)) this.bySegment.delete(id);
    return (this.ranges = ranges);
  }
}
