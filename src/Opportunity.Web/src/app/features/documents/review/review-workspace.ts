import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
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
import { firstValueFrom } from 'rxjs';
import type { FieldResource, SearchHit } from '../../../core/api/generated/models';
import { CommandRegionDirective, CommandRegistry } from '../../../core/commands';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Announcer, Button, DialogService, Icon, IconButton, SplitPane } from '../../../ui';
import { DocumentLoader, LoadedDocument } from './document-loader';
import { CursorDirection, MoveResult, ReviewCursor } from './review-cursor';
import { CodingApi, DocumentContentApi } from './review-ports';
import { CodingState, ReviewCoding, ReviewRelated } from './review-regions';
import { DocumentViewer, ViewerDocument } from './viewer/document-viewer';
import { UnsavedChangesDialog, UnsavedChoice } from './unsaved-changes-dialog';

/** A message under the review bar; `refreshed` and the end notices clear on the next move. */
interface Notice {
  readonly kind: 'refreshed' | 'end' | 'start';
  readonly message: string;
}

/**
 * Review mode of the Documents page (E16-T03, familiarity guide §3.2): the viewer in the centre, the coding pane
 * on the right and Related Items below the viewer, with the review bar on top: Back to list, "Doc n of ≈N",
 * Previous/Next and the document's identity.
 *
 * - Panes resize and collapse (`SplitPane`); sizes and collapsed state are user preferences that follow the
 *   reviewer across browsers (`pane.review.*`). Each pane is a named region in the region cycle
 *   (Alt+Shift+G / Alt+Shift+B: viewer → coding → related) and a command scope.
 * - The review cursor walks the list the document was opened from, across cursor pages and refreshes (Q-33);
 *   it never wraps. Save & Next / Save & Previous save the coding pane's edits first; any other move with
 *   unsaved edits asks Save / Discard / Cancel.
 * - The next document (its metadata and the first content of its viewer mode) is prefetched through the gateway
 *   with `purpose=prefetch` (never audited as viewed); a view is recorded only once a document is on screen.
 */
@Component({
  selector: 'opp-review-workspace',
  imports: [
    Button,
    CommandRegionDirective,
    DocumentViewer,
    Icon,
    IconButton,
    ReviewCoding,
    ReviewRelated,
    SplitPane,
  ],
  templateUrl: './review-workspace.html',
  styleUrl: './review-workspace.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'review', 'data-command-scope': 'review' },
})
export class ReviewWorkspace {
  readonly cursor = input.required<ReviewCursor>();
  /** The workspace's field catalogue (the coding pane shows its coding fields). */
  readonly fields = input<readonly FieldResource[] | null>(null);
  /** Back to the list (after any unsaved edits were saved or discarded). */
  readonly back = output<void>();

  private readonly loader = inject(DocumentLoader);
  private readonly content = inject(DocumentContentApi);
  private readonly codingApi = inject(CodingApi);
  private readonly announcer = inject(Announcer);
  private readonly dialogs = inject(DialogService);
  private readonly prefs = inject(UiPreferences);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  protected readonly canCode = inject(WorkspaceContext).can(PERMISSIONS.codingWrite);

  private readonly editor = viewChild(ReviewCoding);
  private readonly codingSplit = viewChild.required<SplitPane>('codingSplit');
  private readonly relatedSplit = viewChild.required<SplitPane>('relatedSplit');
  private readonly viewerRegion = viewChild.required<ElementRef<HTMLElement>>('viewerRegion');
  private readonly codingRegion = viewChild.required<ElementRef<HTMLElement>>('codingRegion');
  private readonly relatedRegion = viewChild.required<ElementRef<HTMLElement>>('relatedRegion');
  private readonly codingToggle = viewChild.required<ElementRef<HTMLElement>>('codingToggle');
  private readonly relatedToggle = viewChild.required<ElementRef<HTMLElement>>('relatedToggle');

  protected readonly viewer = signal<ViewerDocument | null>(null);
  protected readonly coding = signal<CodingState>('loading');
  protected readonly notice = signal<Notice | null>(null);
  /** Bumped whenever another document is displayed; late responses for an older one are dropped. */
  private seq = 0;

  private readonly displayedId = computed(() => this.cursor().displayed()?.documentId ?? null);
  protected readonly hit = computed(() => this.cursor().displayed());
  protected readonly positionText = computed(() => {
    const cursor = this.cursor();
    const n = new Intl.NumberFormat(this.prefs.locale());
    const position = cursor.position();
    const total = cursor.total();
    if (cursor.left()) return `Not in the refreshed results · ${total}`;
    return position === null ? `Doc — of ${total}` : `Doc ${n.format(position)} of ${total}`;
  });
  /** Share of the result reviewed up to here, for the progress line under the bar (decorative). */
  protected readonly progress = computed(() => {
    const position = this.cursor().position();
    const total = Number(this.cursor().total().replace(/[^\d]/g, ''));
    return position !== null && total > 0 ? Math.min(100, (100 * position) / total) : 0;
  });

  constructor() {
    const registry = inject(CommandRegistry);
    registry.handle('document.next', () => void this.navigate('next'));
    registry.handle('document.previous', () => void this.navigate('previous'));
    registry.handle('review.saveAndNext', () => void this.saveAndMove('next'));
    registry.handle('review.saveAndPrevious', () => void this.saveAndMove('previous'));
    registry.handle('review.save', () => void this.editor()?.save(), {
      enabled: () => this.canCode,
    });
    registry.handle('review.cancelEdits', () => this.editor()?.discard());
    registry.handle('review.backToList', () => void this.leave());
    registry.handle('coding.focus', () => this.focusPane('coding'));
    registry.handle('related.focus', () => this.focusPane('related'));

    effect(() => {
      const id = this.displayedId();
      if (id) untracked(() => this.show(this.cursor().displayed()!));
    });

    // Prefetch the next document once the current one is on screen; keep only the neighbours.
    effect(() => {
      const current = this.viewer();
      if (current?.state !== 'ready') return;
      const cursor = this.cursor();
      const next = cursor.related() ? null : cursor.peek('next');
      const previous = cursor.related() ? null : cursor.peek('previous');
      untracked(() => {
        const keep = [current.hit.documentId, next?.documentId, previous?.documentId];
        this.loader.retain(keep.filter((id): id is string => !!id));
        if (next) this.loader.prefetch(next.documentId);
        else if (!cursor.related()) cursor.loadAhead();
      });
    });

    afterNextRender(() => this.viewerRegion().nativeElement.focus());
  }

  /** The results were refreshed while reviewing (Q-33). */
  notify(message: string): void {
    this.notice.set({ kind: 'refreshed', message });
  }

  // ── Moving ───────────────────────────────────────────────────────────────────────────────────────────────

  /** Previous / Next (bar, keys): asks about unsaved edits first. */
  protected async navigate(direction: CursorDirection): Promise<void> {
    if (await this.confirmLeave()) await this.step(direction);
  }

  /** Save & Next / Save & Previous: saves the coding pane's edits, then moves. */
  protected async saveAndMove(direction: CursorDirection): Promise<void> {
    const editor = this.editor();
    if (editor?.dirty() && !(await editor.save())) return;
    await this.step(direction);
  }

  /** "Continue from next" after the current document left the refreshed results. */
  protected continueFromNext(): void {
    void this.navigate('next');
  }

  protected returnToCursor(): void {
    this.cursor().returnToCursor();
  }

  protected async leave(): Promise<void> {
    if (await this.confirmLeave()) this.back.emit();
  }

  private async step(direction: CursorDirection): Promise<void> {
    const cursor = this.cursor();
    // Related items never move the cursor: Previous/Next carry on from the cursor document.
    cursor.returnToCursor();
    const result: MoveResult = await cursor[direction]();
    switch (result) {
      case 'moved': {
        this.notice.set(null);
        const hit = cursor.hit();
        this.announcer.announce(`${this.positionText()}: ${hit?.controlNumber ?? ''}`);
        break;
      }
      case 'end':
        this.notice.set({ kind: 'end', message: 'End of list. There are no more documents.' });
        this.announcer.announce('End of list.');
        break;
      case 'start':
        this.notice.set({ kind: 'start', message: 'Start of list.' });
        this.announcer.announce('Start of list.');
        break;
      case 'busy':
        this.announcer.announce('Loading more documents. Try again in a moment.');
        break;
    }
  }

  /** True when the displayed document may be left: no unsaved edits, or they were saved or discarded. */
  private async confirmLeave(): Promise<boolean> {
    const editor = this.editor();
    if (!editor?.dirty()) return true;
    const ref = this.dialogs.open<UnsavedChoice>(UnsavedChangesDialog, {
      role: 'alertdialog',
      autoFocus: '[data-autofocus]',
    });
    const choice = (await firstValueFrom(ref.closed)) ?? 'cancel';
    if (choice === 'save') return editor.save();
    if (choice === 'discard') editor.discard();
    return choice === 'discard';
  }

  // ── Showing a document ───────────────────────────────────────────────────────────────────────────────────

  private show(hit: SearchHit): void {
    const seq = ++this.seq;
    const id = hit.documentId;
    const loaded = this.loader.loaded(id);
    this.viewer.set({ hit, state: loaded ? 'ready' : 'loading', content: loaded ?? null });
    if (loaded) {
      this.displayed(seq, loaded);
    } else {
      this.loader.display(id).then(
        (content) => {
          if (seq !== this.seq) return;
          this.viewer.set({ hit, state: 'ready', content });
          this.displayed(seq, content);
        },
        () => {
          if (seq === this.seq) this.viewer.set({ hit, state: 'unavailable', content: null });
        },
      );
    }
    this.coding.set('loading');
    this.codingApi.get(id).then(
      (coding) => seq === this.seq && this.coding.set(coding),
      () => seq === this.seq && this.coding.set('unavailable'),
    );
  }

  /** Once the document is on screen, the view is recorded (`Document.Viewed`) with the delivery's retrieval id. */
  private displayed(seq: number, loaded: LoadedDocument): void {
    afterNextRender(
      () => {
        if (seq !== this.seq) return;
        this.content.recordView(loaded.documentId, loaded.retrievalId).catch(() => undefined);
      },
      { injector: this.injector },
    );
  }

  // ── Panes ────────────────────────────────────────────────────────────────────────────────────────────────

  protected togglePane(pane: 'coding' | 'related'): void {
    const split = pane === 'coding' ? this.codingSplit() : this.relatedSplit();
    const region = pane === 'coding' ? this.codingRegion() : this.relatedRegion();
    const hadFocus = region.nativeElement.contains(this.document.activeElement);
    split.toggleCollapsed();
    // Hidden from its own header: focus moves to the bar's button that shows it again.
    if (hadFocus && split.isCollapsed()) {
      const toggle = pane === 'coding' ? this.codingToggle() : this.relatedToggle();
      toggle.nativeElement.focus();
    }
  }

  /** Focuses a pane's region (opening it first when collapsed). */
  private focusPane(pane: 'coding' | 'related'): void {
    const split = pane === 'coding' ? this.codingSplit() : this.relatedSplit();
    split.toggleCollapsed(false);
    const region = pane === 'coding' ? this.codingRegion() : this.relatedRegion();
    afterNextRender(() => region.nativeElement.focus(), { injector: this.injector });
  }
}
