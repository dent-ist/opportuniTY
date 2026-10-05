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
  HIGHLIGHT_COLOR_LABELS,
  HighlightState,
  highlightColor,
} from '../../../../core/highlights/highlight-sets';
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
import { DocumentContentApi, TextChunk, TextHit, TextHitUnit } from '../review-ports';
import type { ViewerModeDefinition } from './viewer-modes';
import { TextSegment, TextSegments, findPattern, matchOffsets, segmentAt } from './text-segments';

/** Find matches highlighted per block; more are counted only up to this many (a pathological find). */
const MATCHES_PER_SEGMENT = 1000;

/** Highlight Sets per hits request (the API's limit). */
const MAX_SETS = 20;

/** Highlight names of the CSS Custom Highlight API (styled in text-view.scss with `::highlight()`). */
const HIGHLIGHT = { find: 'opp-find', current: 'opp-find-current' } as const;
const COLORS = Object.keys(HIGHLIGHT_COLOR_LABELS);
const colorHighlight = (color: string) => `opp-hl-${highlightColor(color)}`;

/** A group of the highlight bar: the search's hits or one Highlight Set, with its units and total. */
interface HitGroup {
  readonly key: string;
  readonly label: string;
  readonly on: boolean;
  readonly count: number;
  readonly units: readonly TextHitUnit[];
  readonly highlightSetId: string | null;
}

/**
 * Extracted Text mode (E16-T04, familiarity guide §3.2): the document's text streamed in 256 KiB chunks through the
 * gateway (`…/text/chunks/{n}`, purpose display). The first chunk is on screen as soon as it arrives; the next one
 * loads when the reader nears the end of what is loaded, so a 10 MB text never loads or lays out at once. Text is
 * rendered in blocks that the browser lays out only near the viewport (`content-visibility: auto`).
 *
 * - Find in document (Ctrl/⌘+F inside the viewer): matches in the loaded text, "Search whole document" loads
 *   the rest first. Enter / Shift+Enter (or F3 / Shift+F3) step through matches.
 * - Term hits (E16-T12): the server computes the hits of the current search and of the reviewer's Highlight Sets
 *   over the whole text (`…/text/hits`, a page of chunks at a time), so phrases and W/n proximities are whole
 *   spans and counts cover text not loaded yet. Hits show with colour and an underline (palette tokens);
 *   F3 / Shift+F3 (n / Shift+N) step through them across chunks, loading the text up to the hit, and announce
 *   'Hit 4 of 37 "termination"'. The highlight bar toggles Search hits and each set (persisted per user);
 *   Alt+Shift+H switches all highlighting off and on. Image mode says highlighting is in this mode only.
 * - Banners for text truncated for search (Q-29) and for characters that could not be decoded.
 */
@Component({
  selector: 'opp-viewer-text',
  imports: [Button, Checkbox, EmptyState, ErrorState, Icon, IconButton, LoadingState],
  templateUrl: './text-view.html',
  styleUrls: ['./viewer-shared.scss', './text-view.scss', './text-hits.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'viewer-mode' },
})
export class ViewerText {
  readonly documentId = input.required<string>();
  readonly controlNumber = input.required<string>();
  /** Chunk 0 when the document loader already has it (no second request). */
  readonly initial = input<TextChunk | null>(null);
  /** The current search's handle: its hits are highlighted ("Search hits"). */
  readonly searchId = input<string | null>(null);
  /** The other modes the document has, offered when it turns out to have no text. */
  readonly otherModes = input<readonly ViewerModeDefinition[]>([]);
  readonly modeRequest = output<ViewerModeDefinition['mode']>();

  private readonly api = inject(DocumentContentApi);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  protected readonly highlights = inject(HighlightState, { optional: true });
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private readonly findInput = viewChild<ElementRef<HTMLInputElement>>('findInput');

  protected readonly state = signal<'loading' | 'ready' | 'missing' | 'error'>('loading');
  protected readonly error = signal<ApiError | null>(null);
  private readonly chunks = signal<readonly TextChunk[]>([]);
  protected readonly segments = signal<readonly TextSegment[]>([]);
  /** Loading the next chunk: idle, on its way, or failed (a retry button is shown). */
  protected readonly more = signal<'idle' | 'loading' | 'error'>('idle');
  /** "Search whole document" (or the way to a hit) progress: chunks loaded of all, while it runs. */
  protected readonly loadingAll = signal<{ loaded: number; total: number; why: string } | null>(
    null,
  );

  private readonly firstChunk = computed(() => this.chunks()[0] ?? null);
  protected readonly complete = computed(() => this.chunks().at(-1)?.isLast ?? false);
  protected readonly truncated = computed(() => this.firstChunk()?.truncated ?? false);
  protected readonly encodingWarning = computed(() => this.firstChunk()?.encodingWarning ?? false);
  protected readonly progressText = computed(() => {
    const last = this.chunks().at(-1);
    return last ? `Part ${last.index + 1} of ${last.count} loaded` : '';
  });
  /** Where each loaded chunk starts in the whole text (UTF-16), by chunk index. */
  private chunkStarts: number[] = [];
  private loadedLength = 0;

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

  // ── Term hits ────────────────────────────────────────────────────────────────────────────────────────────
  /** All highlighting on (Alt+Shift+H switches it off for the session). */
  protected readonly showHits = signal(true);
  protected readonly units = signal<readonly TextHitUnit[]>([]);
  private readonly allHits = signal<readonly TextHit[]>([]);
  protected readonly hitsState = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  /** Chunks counted of all while the hits are still being computed. */
  protected readonly hitsProgress = signal<{ done: number; total: number } | null>(null);
  protected readonly searchExpired = signal(false);
  protected readonly panelOpen = signal(false);
  protected readonly hitIndex = signal(-1);

  private readonly enabledSetIds = computed(() =>
    (this.highlights?.enabledSets() ?? []).slice(0, MAX_SETS).map((s) => s.highlightSetId),
  );
  private readonly unitVisible = computed(() => {
    const searchOn = this.highlights?.searchHits() ?? true;
    const sets = new Set(this.enabledSetIds());
    return this.units().map((u) =>
      u.source === 'search' ? searchOn : sets.has(u.highlightSetId ?? ''),
    );
  });
  /** The hits shown and stepped through, in text order. */
  protected readonly visibleHits = computed(() => {
    if (!this.showHits()) return [];
    const visible = this.unitVisible();
    return this.allHits().filter((h) => visible[h.unit]);
  });
  protected readonly hitCount = computed(() => this.visibleHits().length);
  protected readonly hasHighlighting = computed(
    () => this.units().length > 0 || (this.highlights?.sets().length ?? 0) > 0 || !!this.searchId(),
  );
  protected readonly hitLabel = computed(() => {
    const count = this.hitCount();
    const counting = this.hitsState() === 'loading' ? ' so far' : '';
    if (!this.showHits()) return 'Highlighting off';
    if (count === 0) return this.hitsState() === 'loading' ? 'Counting hits…' : 'No hits';
    const index = this.hitIndex();
    if (index >= 0 && index < count) {
      return `Hit ${index + 1} of ${count}${counting} "${this.labelOf(this.visibleHits()[index])}"`;
    }
    return `${count} ${count === 1 ? 'hit' : 'hits'}${counting}`;
  });
  /** The bar's groups: Search hits, then each Highlight Set, with per-unit counts. */
  protected readonly groups = computed<HitGroup[]>(() => {
    const units = this.units();
    const groups: HitGroup[] = [];
    const total = (list: readonly TextHitUnit[]) => list.reduce((n, u) => n + u.count, 0);
    if (this.searchId()) {
      const search = units.filter((u) => u.source === 'search');
      groups.push({
        key: 'search',
        label: 'Search hits',
        on: this.highlights?.searchHits() ?? true,
        count: total(search),
        units: search,
        highlightSetId: null,
      });
    }
    for (const set of this.highlights?.sets() ?? []) {
      const mine = units.filter((u) => u.highlightSetId === set.highlightSetId);
      groups.push({
        key: set.highlightSetId,
        label: set.name,
        on: this.highlights!.isOn(set.highlightSetId),
        count: total(mine),
        units: mine,
        highlightSetId: set.highlightSetId,
      });
    }
    return groups;
  });
  protected readonly colorLabel = (color: string) => HIGHLIGHT_COLOR_LABELS[highlightColor(color)];
  protected readonly swatch = (color: string) => highlightColor(color);

  private segmenter = new TextSegments();
  private readonly findRanges = new RangeIndex();
  private hitRanges = new Map<TextHit, Range>();
  /** Bumped when another document is shown; late chunk responses for an older one are dropped. */
  private seq = 0;
  /** Bumped when the hits are asked for again (document, search or toggles changed). */
  private hitsSeq = 0;
  private observer?: IntersectionObserver;

  constructor() {
    void this.highlights?.load();

    effect(() => {
      const documentId = this.documentId();
      const initial = this.initial();
      untracked(() => this.start(documentId, initial));
    });

    // The hits follow the document, the search and the sets that are on.
    effect(() => {
      const documentId = this.documentId();
      const searchId = this.searchId();
      const sets = this.enabledSetIds();
      untracked(() => void this.loadHits(documentId, searchId, sets));
    });

    // Highlights follow the loaded text, the find text, the hits and the toggles (after the DOM has them).
    effect(() => {
      this.segments();
      this.query();
      this.visibleHits();
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
    registry.handle('viewer.nextHit', () => void this.step(1), { enabled: () => this.canStep() });
    registry.handle('viewer.previousHit', () => void this.step(-1), {
      enabled: () => this.canStep(),
    });
    registry.handle('viewer.toggleHighlights', () => this.toggleHits(), {
      enabled: () => this.hasHighlighting(),
    });

    inject(DestroyRef).onDestroy(() => {
      this.seq++;
      this.hitsSeq++;
      this.observer?.disconnect();
      const registry = highlightRegistry();
      for (const name of [...Object.values(HIGHLIGHT), ...COLORS.map(colorHighlight)]) {
        registry?.delete(name);
      }
    });
  }

  // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────

  private start(documentId: string, initial: TextChunk | null): void {
    const seq = ++this.seq;
    this.segmenter = new TextSegments();
    this.chunkStarts = [];
    this.loadedLength = 0;
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
    this.chunkStarts[chunk.index] = this.loadedLength;
    this.loadedLength += chunk.text.length;
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

  /** Loads chunks one at a time until `chunk` is loaded (or the text ends); false when it was not reached. */
  private async loadThrough(chunk: number, why: string): Promise<boolean> {
    const seq = this.seq;
    while (seq === this.seq) {
      const last = this.chunks().at(-1);
      if (!last) return false;
      if (last.index >= chunk || last.isLast) break;
      this.loadingAll.set({ loaded: last.index + 1, total: last.count, why });
      if (!(await this.loadNext())) break;
      // Let the browser paint between chunks, so a long text never blocks the page.
      await new Promise((resolve) => setTimeout(resolve));
    }
    if (seq !== this.seq) return false;
    this.loadingAll.set(null);
    return (this.chunks().at(-1)?.index ?? -1) >= chunk || this.complete();
  }

  /** "Search whole document": loads every remaining chunk, one at a time, then shows the first match. */
  protected async loadAll(): Promise<void> {
    const seq = this.seq;
    await this.loadThrough(Number.MAX_SAFE_INTEGER, 'Loading the rest of the text to search it');
    if (seq !== this.seq) return;
    afterNextRender(
      () => {
        this.updateHighlights();
        if (this.query().trim() && this.findCount() > 0) this.stepFind(1);
      },
      { injector: this.injector },
    );
  }

  /** Every page of the document's hits; units and counts add up page by page, shown as they come. */
  private async loadHits(
    documentId: string,
    searchId: string | null,
    highlightSetIds: readonly string[],
  ): Promise<void> {
    const seq = ++this.hitsSeq;
    this.units.set([]);
    this.allHits.set([]);
    this.hitIndex.set(-1);
    this.hitsProgress.set(null);
    this.searchExpired.set(false);
    if (!searchId && highlightSetIds.length === 0) {
      this.hitsState.set('idle');
      return;
    }
    this.hitsState.set('loading');
    let search = searchId;
    let from: number | null = 0;
    try {
      while (from !== null) {
        let page;
        try {
          page = await this.api.textHits(documentId, {
            searchId: search,
            highlightSetIds,
            fromChunk: from,
          });
        } catch (e) {
          // An expired search: keep the Highlight Sets and say why the search's hits are gone.
          if (search && toApiError(e).status === 404 && from === 0) {
            if (seq !== this.hitsSeq) return;
            this.searchExpired.set(true);
            search = null;
            if (!highlightSetIds.length) {
              this.hitsState.set('ready');
              return;
            }
            continue;
          }
          throw e;
        }
        if (seq !== this.hitsSeq) return;
        const previous = this.units();
        this.units.set(
          page.units.map((u) => ({ ...u, count: (previous[u.unit]?.count ?? 0) + u.count })),
        );
        if (page.hits.length) this.allHits.update((list) => [...list, ...page.hits]);
        from = page.nextChunk;
        this.hitsProgress.set(from === null ? null : { done: from, total: page.chunkCount });
      }
      this.hitsState.set('ready');
    } catch {
      if (seq === this.hitsSeq) this.hitsState.set('error');
    }
  }

  protected retryHits(): void {
    void this.loadHits(this.documentId(), this.searchId(), this.enabledSetIds());
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
    return this.query().trim() ? this.findCount() > 0 : this.hitCount() > 0;
  }

  /** F3 / Shift+F3 (n / Shift+N): the find matches while there is a find text, else the hits. */
  private async step(delta: 1 | -1): Promise<void> {
    if (this.query().trim()) this.stepFind(delta);
    else await this.stepHit(delta, true);
  }

  protected stepFind(delta: 1 | -1): void {
    // Enter right after typing comes before the highlights caught up with the new find text.
    this.updateHighlights();
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

  /** The next or previous hit, loading the text up to it when it lies beyond what is loaded. */
  protected async stepHit(delta: 1 | -1, focusText = false): Promise<void> {
    const hits = this.visibleHits();
    if (!hits.length) return;
    const index = wrap(this.hitIndex() + delta, hits.length, this.hitIndex() < 0 && delta < 0);
    await this.goToHit(index, focusText);
  }

  /** The next hit of one unit after the current position (the per-term "next" of the bar). */
  protected async stepUnit(unit: TextHitUnit): Promise<void> {
    const hits = this.visibleHits();
    const current = this.hitIndex();
    const after = hits.findIndex((h, i) => i > current && h.unit === unit.unit);
    const index = after >= 0 ? after : hits.findIndex((h) => h.unit === unit.unit);
    if (index >= 0) await this.goToHit(index, false);
  }

  private async goToHit(index: number, focusText: boolean): Promise<void> {
    const hit = this.visibleHits()[index];
    const seq = this.seq;
    // The span may run into the next chunk: that one has to be on the page too.
    const needed = this.spanEnd(hit) === null ? hit.chunk + 1 : hit.chunk;
    if (!(await this.loadThrough(needed, 'Loading the text up to the hit')) || seq !== this.seq) {
      return;
    }
    this.hitIndex.set(index);
    afterNextRender(
      () => {
        this.updateHighlights();
        const range = this.hitRanges.get(hit);
        if (range) {
          if (focusText) this.scroller()?.nativeElement.focus({ preventScroll: true });
          this.reveal(range);
        }
        const count = this.visibleHits().length;
        this.announcer.announce(`Hit ${index + 1} of ${count} "${this.labelOf(hit)}"`);
      },
      { injector: this.injector },
    );
  }

  protected toggleHits(): void {
    this.showHits.update((v) => !v);
    this.announcer.announce(this.showHits() ? 'Highlighting on' : 'Highlighting off');
  }

  protected toggleGroup(group: HitGroup, on: boolean): void {
    this.hitIndex.set(-1);
    if (group.highlightSetId) this.highlights?.setSet(group.highlightSetId, on);
    else this.highlights?.setSearchHits(on);
  }

  private labelOf(hit: TextHit): string {
    return this.units()[hit.unit]?.label ?? '';
  }

  /** The hit's end in the whole text, or null while the chunk it ends in is not loaded. */
  private spanEnd(hit: TextHit): number | null {
    const start = this.chunkStarts[hit.chunk];
    if (start === undefined) return null;
    const end = start + hit.end;
    return end <= this.loadedLength ? end : null;
  }

  private updateHighlights(): void {
    const root = this.scroller()?.nativeElement;
    if (!root) return;
    const find = this.findRanges.update(root, findPattern(this.query()), MATCHES_PER_SEGMENT);
    this.findCount.set(find.length);
    if (this.findIndex() >= find.length) this.findIndex.set(-1);
    const byColor = this.buildHitRanges(root);
    const registry = highlightRegistry();
    if (!registry) return;
    registry.set(HIGHLIGHT.find, new Highlight(...find));
    for (const color of COLORS) {
      registry.set(colorHighlight(color), new Highlight(...(byColor.get(color) ?? [])));
    }
    const hits = this.visibleHits();
    const current =
      this.query().trim() && this.findIndex() >= 0
        ? find[this.findIndex()]
        : this.hitIndex() >= 0 && hits[this.hitIndex()]
          ? this.hitRanges.get(hits[this.hitIndex()])
          : undefined;
    this.paintCurrent(current);
  }

  /** Ranges of the visible hits within the loaded text, grouped by palette colour. */
  private buildHitRanges(root: HTMLElement): Map<string, Range[]> {
    const byColor = new Map<string, Range[]>();
    const ranges = new Map<TextHit, Range>();
    const segments = this.segments();
    const units = this.units();
    const blocks = new Map<string, Text>();
    for (const block of root.querySelectorAll<HTMLElement>('[data-segment]')) {
      const node = block.firstChild;
      if (node && node.nodeType === Node.TEXT_NODE)
        blocks.set(block.dataset['segment']!, node as Text);
    }
    for (const hit of this.visibleHits()) {
      const end = this.spanEnd(hit);
      if (end === null) continue;
      const start = this.chunkStarts[hit.chunk] + hit.start;
      const first = segmentAt(segments, start);
      const last = segmentAt(segments, end - 1);
      const startNode = first >= 0 ? blocks.get(String(segments[first].id)) : undefined;
      const endNode = last >= 0 ? blocks.get(String(segments[last].id)) : undefined;
      if (!startNode || !endNode) continue;
      const range = root.ownerDocument.createRange();
      range.setStart(startNode, start - segments[first].start);
      range.setEnd(endNode, end - segments[last].start);
      ranges.set(hit, range);
      const color = highlightColor(units[hit.unit]?.color);
      const list = byColor.get(color);
      if (list) list.push(range);
      else byColor.set(color, [range]);
    }
    this.hitRanges = ranges;
    return byColor;
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
