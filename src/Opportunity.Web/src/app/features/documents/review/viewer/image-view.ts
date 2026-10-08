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
  model,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import type {
  DocumentImagesInfo,
  DocumentPageResource,
} from '../../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../../core/api/problem-details';
import { CommandRegistry } from '../../../../core/commands';
import {
  Announcer,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
} from '../../../../ui';
import { DocumentContentApi } from '../review-ports';
import { NORMALIZED_SCALE } from './redaction/redaction-api';
import { defaultFrameBox, minimumSize } from './redaction/redaction-geometry';
import { RedactionLayer } from './redaction/redaction-layer';
import { RedactionPanel } from './redaction/redaction-panel';
import { type EditableRedaction, RedactionSession } from './redaction/redaction-session';

/** Zoom steps in percent (25–400 %, E16-T04). */
export const ZOOM_STEPS: readonly number[] = [25, 33, 50, 67, 75, 100, 125, 150, 200, 300, 400];
const MIN_ZOOM = ZOOM_STEPS[0];
const MAX_ZOOM = ZOOM_STEPS.at(-1)!;
/** CSS pixels per point at 100 %. */
const PX_PER_PT = 96 / 72;
/** Space around the page inside the stage, in CSS pixels (both sides together). */
const STAGE_PADDING = 32;

export type FitMode = 'width' | 'page' | null;

interface PageImage {
  readonly pageNumber: number;
  readonly url: string;
}

/**
 * Image mode (E16-T04, familiarity guide §3.2): the active page set's images through the gateway, one page at a
 * time, with thumbnails, go to page, previous/next page (PageUp/PageDown inside the viewer), zoom 25–400 %, fit
 * width / fit page and rotate. Images are fetched as bytes and shown through object URLs that are revoked as
 * soon as the page or document changes, so a copied image address stops working (no long-lived content URLs).
 * The next page is fetched ahead with `purpose=prefetch`.
 */
@Component({
  selector: 'opp-viewer-image',
  imports: [
    Button,
    EmptyState,
    ErrorState,
    Icon,
    IconButton,
    LoadingState,
    RedactionLayer,
    RedactionPanel,
  ],
  templateUrl: './image-view.html',
  styleUrls: ['./viewer-shared.scss', './image-view.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'viewer-mode' },
  providers: [RedactionSession],
})
export class ViewerImage {
  readonly documentId = input.required<string>();
  readonly controlNumber = input.required<string>();
  readonly images = input.required<DocumentImagesInfo>();
  /** The page list and first image when the document loader already has them. */
  readonly initialPages = input<readonly DocumentPageResource[] | null>(null);
  readonly initialImage = input<{ readonly pageNumber: number; readonly blob: Blob } | null>(null);
  /** Redaction mode (E11-T04): the redaction overlay, tools and list are shown; kept across documents. */
  readonly redactionMode = model(false);

  private readonly api = inject(DocumentContentApi);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly stage = viewChild<ElementRef<HTMLElement>>('stage');
  private readonly thumbList = viewChild<ElementRef<HTMLElement>>('thumbs');
  private readonly layer = viewChild(RedactionLayer);
  private readonly dialogs = inject(DialogService);
  protected readonly redaction = inject(RedactionSession);
  /** Production preview of the redactions (opaque, as burned). */
  protected readonly redactionPreview = signal(false);
  /** The smallest redaction on the current page (2 × 2 pixels at 300 DPI). */
  protected readonly minRedaction = computed(() => {
    const page = this.page();
    return minimumSize(Number(page?.widthPt) || 612, Number(page?.heightPt) || 792);
  });

  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly error = signal<ApiError | null>(null);
  protected readonly pages = signal<readonly DocumentPageResource[]>([]);
  /** Index of the page shown, in `pages`. */
  protected readonly index = signal(0);
  protected readonly page = computed(() => this.pages()[this.index()] ?? null);
  protected readonly pageNumber = computed(() => Number(this.page()?.pageNumber ?? 1));
  protected readonly image = signal<PageImage | 'loading' | 'error' | null>(null);
  protected readonly thumbsOpen = signal(true);
  protected readonly thumbUrls = signal<Readonly<Record<number, string>>>({});

  /** Zoom chosen with zoom in/out; ignored while a fit mode is on. */
  private readonly manualZoom = signal(100);
  protected readonly fit = signal<FitMode>('width');
  /** The reviewer's extra rotation per page number (degrees, clockwise). */
  private readonly rotations = signal<Readonly<Record<number, number>>>({});
  private readonly stageSize = signal({ width: 0, height: 0 });

  protected readonly rotation = computed(() => {
    const page = this.page();
    if (!page) return 0;
    return (
      ((((Number(page.rotation) || 0) + (this.rotations()[this.pageNumber()] ?? 0)) % 360) + 360) %
      360
    );
  });
  /** The page at 100 %, in CSS pixels, before rotation. */
  private readonly natural = computed(() => {
    const page = this.page();
    const width = Number(page?.widthPt) || Number(page?.imageWidthPx) * 0.75 || 612;
    const height = Number(page?.heightPt) || Number(page?.imageHeightPx) * 0.75 || 792;
    return { width: width * PX_PER_PT, height: height * PX_PER_PT };
  });
  private readonly sideways = computed(() => this.rotation() % 180 !== 0);
  protected readonly zoom = computed(() => {
    const fit = this.fit();
    const { width: stageWidth, height: stageHeight } = this.stageSize();
    if (!fit || stageWidth <= 0) return fit ? 100 : this.manualZoom();
    const natural = this.natural();
    const [w, h] = this.sideways()
      ? [natural.height, natural.width]
      : [natural.width, natural.height];
    const byWidth = ((stageWidth - STAGE_PADDING) / w) * 100;
    const byHeight = ((stageHeight - STAGE_PADDING) / h) * 100;
    return clampZoom(fit === 'width' ? byWidth : Math.min(byWidth, byHeight));
  });
  protected readonly zoomText = computed(() => `${Math.round(this.zoom())}%`);
  /** Size of the image element (unrotated) and of its frame (the rotated bounding box). */
  protected readonly box = computed(() => {
    const { width, height } = this.natural();
    const scale = this.zoom() / 100;
    const w = Math.round(width * scale);
    const h = Math.round(height * scale);
    return this.sideways()
      ? { imgWidth: w, imgHeight: h, frameWidth: h, frameHeight: w }
      : { imgWidth: w, imgHeight: h, frameWidth: w, frameHeight: h };
  });
  protected readonly imageTransform = computed(
    () => `translate(-50%, -50%) rotate(${this.rotation()}deg)`,
  );

  /** Object URLs of fetched page images, by page number (current and neighbours only). */
  private readonly cache = new Map<number, PageImage>();
  private readonly loadingPages = new Set<number>();
  private seq = 0;
  private thumbObserver?: IntersectionObserver;
  private resizeObserver?: ResizeObserver;

  constructor() {
    effect(() => {
      const documentId = this.documentId();
      const pages = this.initialPages();
      const first = this.initialImage();
      untracked(() => this.start(documentId, pages, first));
    });

    // Show the current page; keep its neighbours, fetch the next one ahead.
    effect(() => {
      const page = this.page();
      if (this.state() !== 'ready' || !page) return;
      untracked(() => this.show(page));
    });

    // Fit modes follow the size of the stage.
    effect(() => {
      const stage = this.stage()?.nativeElement;
      this.resizeObserver?.disconnect();
      if (!stage) return;
      const measure = () =>
        this.stageSize.set({ width: stage.clientWidth, height: stage.clientHeight });
      measure();
      if (typeof ResizeObserver !== 'undefined') {
        this.resizeObserver = new ResizeObserver(measure);
        this.resizeObserver.observe(stage);
      }
    });

    // Thumbnails load when they scroll into view.
    effect(() => {
      const list = this.thumbList()?.nativeElement;
      this.pages();
      this.thumbObserver?.disconnect();
      if (!list || typeof IntersectionObserver === 'undefined') return;
      const observer = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            if (!entry.isIntersecting) continue;
            observer.unobserve(entry.target);
            untracked(() => this.loadThumb(Number((entry.target as HTMLElement).dataset['thumb'])));
          }
        },
        { root: list, rootMargin: '200px 0px' },
      );
      this.thumbObserver = observer;
      afterNextRender(
        () => list.querySelectorAll('[data-thumb]').forEach((el) => observer.observe(el)),
        { injector: this.injector },
      );
    });

    // Redaction mode loads the document's redactions; leaving it (or the mode) discards the editor state.
    effect(() => {
      const documentId = this.documentId();
      const on = this.redactionMode();
      untracked(() => (on ? void this.redaction.open(documentId) : this.redaction.close()));
    });

    const registry = inject(CommandRegistry);
    const ready = () => this.state() === 'ready' && this.pages().length > 0;
    const drawing = () => ready() && this.redactionMode() && this.redaction.canDraw();
    registry.handle('redaction.newBox', () => this.newRedaction(), { enabled: drawing });
    registry.handle('redaction.fullPage', () => void this.redactFullPage(), { enabled: drawing });
    registry.handle('viewer.nextPage', () => this.stepPage(1), { enabled: ready });
    registry.handle('viewer.previousPage', () => this.stepPage(-1), { enabled: ready });
    registry.handle('viewer.zoomIn', () => this.zoomBy(1), { enabled: ready });
    registry.handle('viewer.zoomOut', () => this.zoomBy(-1), { enabled: ready });
    registry.handle('viewer.zoomFit', () => this.setFit('width'), { enabled: ready });
    registry.handle('viewer.rotate', () => this.rotate(), { enabled: ready });

    inject(DestroyRef).onDestroy(() => {
      this.redaction.close();
      this.seq++;
      this.thumbObserver?.disconnect();
      this.resizeObserver?.disconnect();
      this.revokeAll();
    });
  }

  // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────

  private start(
    documentId: string,
    pages: readonly DocumentPageResource[] | null,
    first: { pageNumber: number; blob: Blob } | null,
  ): void {
    const seq = ++this.seq;
    this.revokeAll();
    this.rotations.set({});
    this.image.set(null);
    if (first)
      this.cache.set(first.pageNumber, {
        pageNumber: first.pageNumber,
        url: objectUrl(first.blob),
      });
    if (pages) {
      this.ready(pages, first?.pageNumber);
      return;
    }
    this.state.set('loading');
    this.api.pages(documentId, 'display').then(
      (list) => seq === this.seq && this.ready(list.value),
      (e) => {
        if (seq !== this.seq) return;
        this.error.set(toApiError(e));
        this.state.set('error');
      },
    );
  }

  protected retry(): void {
    this.start(this.documentId(), null, null);
  }

  private ready(pages: readonly DocumentPageResource[], firstPage?: number): void {
    this.pages.set(pages);
    const start = firstPage ?? Number(pages.find((p) => p.hasImage)?.pageNumber ?? 1);
    this.index.set(
      Math.max(
        0,
        pages.findIndex((p) => Number(p.pageNumber) === start),
      ),
    );
    this.state.set('ready');
  }

  private show(page: DocumentPageResource): void {
    const number = Number(page.pageNumber);
    const cached = this.cache.get(number);
    this.image.set(cached ?? (page.hasImage ? 'loading' : null));
    if (!cached && page.hasImage) void this.fetchPage(number, 'display');
    // Keep the neighbours; fetch the next page ahead.
    const keep = new Set([number - 1, number, number + 1]);
    for (const [n, image] of this.cache) {
      if (!keep.has(n)) {
        URL.revokeObjectURL(image.url);
        this.cache.delete(n);
      }
    }
    const next = this.pages()[this.index() + 1];
    if (next?.hasImage && !this.cache.has(Number(next.pageNumber))) {
      void this.fetchPage(Number(next.pageNumber), 'prefetch');
    }
  }

  private async fetchPage(pageNumber: number, purpose: 'display' | 'prefetch'): Promise<void> {
    if (this.loadingPages.has(pageNumber)) return;
    const seq = this.seq;
    this.loadingPages.add(pageNumber);
    try {
      const delivered = await this.api.pageImage(this.documentId(), pageNumber, 'image', purpose);
      if (seq !== this.seq) return;
      const image = { pageNumber, url: objectUrl(delivered.value) };
      const previous = this.cache.get(pageNumber);
      if (previous) URL.revokeObjectURL(previous.url);
      this.cache.set(pageNumber, image);
      if (this.pageNumber() === pageNumber) this.image.set(image);
    } catch {
      if (seq === this.seq && this.pageNumber() === pageNumber) this.image.set('error');
    } finally {
      if (seq === this.seq) this.loadingPages.delete(pageNumber);
    }
  }

  protected retryPage(): void {
    void this.fetchPage(this.pageNumber(), 'display');
    this.image.set('loading');
  }

  private async loadThumb(pageNumber: number): Promise<void> {
    const page = this.pages().find((p) => Number(p.pageNumber) === pageNumber);
    if (!page?.hasThumbnail || this.thumbUrls()[pageNumber]) return;
    const seq = this.seq;
    try {
      const delivered = await this.api.pageImage(
        this.documentId(),
        pageNumber,
        'thumbnail',
        'display',
      );
      if (seq !== this.seq) return;
      this.thumbUrls.update((urls) => ({ ...urls, [pageNumber]: objectUrl(delivered.value) }));
    } catch {
      // A thumbnail that cannot be loaded keeps its page number only.
    }
  }

  private revokeAll(): void {
    for (const image of this.cache.values()) URL.revokeObjectURL(image.url);
    this.cache.clear();
    this.loadingPages.clear();
    for (const url of Object.values(this.thumbUrls())) URL.revokeObjectURL(url);
    this.thumbUrls.set({});
  }

  // ── Navigation, zoom, rotation ───────────────────────────────────────────────────────────────────────────

  protected stepPage(delta: 1 | -1): void {
    const target = this.index() + delta;
    if (target < 0 || target >= this.pages().length) {
      this.announcer.announce(delta > 0 ? 'Last page.' : 'First page.');
      return;
    }
    this.goToIndex(target);
  }

  protected goToIndex(index: number): void {
    this.index.set(index);
    this.announcer.announce(`Page ${this.pageNumber()} of ${this.pages().length}`);
    this.stage()?.nativeElement.scrollTo?.({ top: 0, left: 0 });
  }

  protected goToTyped(event: Event): void {
    const input = event.target as HTMLInputElement;
    const wanted = Number.parseInt(input.value, 10);
    const index = this.pages().findIndex((p) => Number(p.pageNumber) === wanted);
    if (index < 0) {
      this.announcer.announce(
        `There is no page ${input.value.trim() || 'with that number'}. Pages 1 to ${this.pages().length}.`,
      );
      input.value = String(this.pageNumber());
      return;
    }
    if (index !== this.index()) this.goToIndex(index);
  }

  protected zoomBy(direction: 1 | -1): void {
    const current = this.zoom();
    const next =
      direction > 0
        ? (ZOOM_STEPS.find((z) => z > current + 0.5) ?? MAX_ZOOM)
        : ([...ZOOM_STEPS].reverse().find((z) => z < current - 0.5) ?? MIN_ZOOM);
    this.manualZoom.set(next);
    this.fit.set(null);
    this.announcer.announce(`Zoom ${next}%`);
  }

  protected setFit(fit: 'width' | 'page'): void {
    this.fit.set(fit);
    this.announcer.announce(fit === 'width' ? 'Fit to width' : 'Fit page');
  }

  protected rotate(): void {
    const n = this.pageNumber();
    this.rotations.update((r) => ({ ...r, [n]: ((r[n] ?? 0) + 90) % 360 }));
    this.announcer.announce(`Page rotated to ${this.rotation()} degrees`);
  }

  protected toggleThumbs(): void {
    this.thumbsOpen.update((v) => !v);
  }

  /** One tab stop for the thumbnails: arrows, Home and End move between pages. */
  protected onThumbKey(event: KeyboardEvent): void {
    const last = this.pages().length - 1;
    const moves: Record<string, number> = {
      ArrowDown: this.index() + 1,
      ArrowRight: this.index() + 1,
      ArrowUp: this.index() - 1,
      ArrowLeft: this.index() - 1,
      Home: 0,
      End: last,
    };
    const target = moves[event.key];
    if (target === undefined) return;
    event.preventDefault();
    const index = Math.max(0, Math.min(last, target));
    if (index !== this.index()) this.goToIndex(index);
    afterNextRender(
      () =>
        this.thumbList()
          ?.nativeElement.querySelector<HTMLElement>(`[data-index="${index}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  // ── Redaction mode ─────────────────────────────────────────────────────────────────────────────────────────

  toggleRedaction(): void {
    const on = !this.redactionMode();
    this.redactionMode.set(on);
    this.announcer.announce(on ? 'Redaction mode on' : 'Redaction mode off');
  }

  /** A centred box on the current page, focused for arrow-key adjustment (the keyboard way to draw). */
  protected newRedaction(): void {
    const layer = this.layer();
    if (!layer) {
      this.announcer.announce(`Page ${this.pageNumber()} has no image to redact.`);
      return;
    }
    layer.addFromKeyboard(defaultFrameBox());
  }

  /** Redacts the whole current page after confirmation (common for privileged attachments). */
  protected async redactFullPage(): Promise<void> {
    const page = this.pageNumber();
    if (!this.layer()) {
      this.announcer.announce(`Page ${page} has no image to redact.`);
      return;
    }
    const reason = this.redaction.reasons().find((r) => r.code === this.redaction.reasonCode());
    const confirmed = await this.dialogs.confirm({
      title: 'Redact full page',
      message: `Redact all of page ${page} as ${reason?.name ?? 'the chosen reason'} (${this.redaction.type() === 'labelled' ? 'labelled box' : 'black box'})?`,
      confirmLabel: 'Redact page',
    });
    if (!confirmed) return;
    const id = this.redaction.add(page, {
      x: 0,
      y: 0,
      w: NORMALIZED_SCALE,
      h: NORMALIZED_SCALE,
    });
    if (id) {
      this.layer()?.focusBox(id);
      this.announcer.announce(`Page ${page} redacted.`);
    }
  }

  /** Shows a redaction from the list: its page, with the redaction selected and focused. */
  protected showRedaction(r: EditableRedaction): void {
    const index = this.pages().findIndex((p) => Number(p.pageNumber) === r.pageNumber);
    this.redaction.select(r.id);
    if (index >= 0 && index !== this.index()) this.goToIndex(index);
    afterNextRender(() => this.layer()?.focusBox(r.id), { injector: this.injector });
  }

  protected pageLabel(page: DocumentPageResource): string {
    return `Page ${page.pageNumber}`;
  }
}

function clampZoom(value: number): number {
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, value));
}

function objectUrl(blob: Blob): string {
  return URL.createObjectURL(blob);
}
