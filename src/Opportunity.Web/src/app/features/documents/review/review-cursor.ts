import { Signal, computed, signal } from '@angular/core';
import type { SearchHit } from '../../../core/api/generated/models';

export type CursorDirection = 'next' | 'previous';

/**
 * The ordered set the cursor walks: the document list's loaded cursor pages (E16-T02). Rows are addressed by
 * index; the source fetches neighbouring pages on demand and may replace its rows when the results refresh.
 */
export interface CursorSource {
  readonly rows: Signal<readonly SearchHit[]>;
  /** "250", "≈ 58,330" or "≥ 10,000 (approx.)" (Q-10, Q-32). */
  readonly countText: Signal<string>;
  positionOf(index: number): number | null;
  hasMore(direction: CursorDirection): boolean;
  /** True when a page was added. The rows can also change otherwise: an expired search runs again (Q-33). */
  fetchMore(direction: CursorDirection): Promise<boolean>;
  /** The handle of the search behind the rows, when there is one (its hits are highlighted, E16-T12). */
  readonly searchId?: Signal<string | null>;
}

/** `end` / `start`: nothing beyond (the cursor never wraps). `busy`: the list is loading; try again. */
export type MoveResult = 'moved' | 'end' | 'start' | 'busy';

/** Neighbours remembered on each move, to carry on after the current document left the set. */
const REMEMBERED = 5;

/**
 * The review cursor (E16-T03, familiarity guide §3.2): the current document's place in the result set the
 * reviewer opened it from. Previous/Next follow the list order across cursor pages, fetching the next page when
 * the loaded rows run out. The cursor holds a document id, never an index, so it survives pages being added
 * above it and the results being refreshed (Q-33). When a refresh drops the current document from the set,
 * `left` turns true and the next move continues from the document that followed it.
 *
 * Opening a related item does not move the cursor: `showRelated` changes what is displayed, Previous/Next still
 * walk the original set, and `returnToCursor` comes back.
 */
export class ReviewCursor {
  private readonly _documentId = signal<string | null>(null);
  private readonly _related = signal<SearchHit | null>(null);
  /** The cursor document's row as last seen (it stays known after the document left the set). */
  private readonly lastHit = signal<SearchHit | null>(null);
  private followers: string[] = [];
  private predecessors: string[] = [];
  private lastPosition: number | null = null;
  private lastIndex = -1;
  private moving = false;

  constructor(private readonly source: CursorSource) {}

  /** The document the cursor is on. */
  readonly documentId = this._documentId.asReadonly();
  readonly index = computed(() => {
    const id = this._documentId();
    return id === null ? -1 : this.source.rows().findIndex((r) => r.documentId === id);
  });
  readonly hit = computed(() => this.source.rows()[this.index()] ?? this.lastHit());
  /** The current document is no longer in the (refreshed) result set. */
  readonly left = computed(() => this._documentId() !== null && this.index() < 0);
  /** 1-based position in the whole result, when known. */
  readonly position = computed(() => {
    const i = this.index();
    return i < 0 ? null : this.source.positionOf(i);
  });
  readonly total = computed(() => this.source.countText());
  /** The search the cursor walks, for highlighting its hits. */
  readonly searchId = computed(() => this.source.searchId?.() ?? null);
  /** The related item on display instead of the cursor document, if any. */
  readonly related = this._related.asReadonly();
  /** What the viewer and coding pane show: the related item, else the cursor document. */
  readonly displayed = computed(() => this._related() ?? this.hit());
  readonly hasNext = computed(() => {
    const i = this.index();
    return this.left() || i < this.source.rows().length - 1 || this.source.hasMore('next');
  });
  readonly hasPrevious = computed(() => {
    const i = this.index();
    return this.left() || i > 0 || this.source.hasMore('previous');
  });

  /** Puts the cursor on `documentId` (a row of the source). */
  open(documentId: string): void {
    this._documentId.set(documentId);
    this._related.set(null);
    this.remember();
  }

  /** The row after the current document, fetching the next cursor page when needed. Never wraps. */
  next(): Promise<MoveResult> {
    return this.move('next');
  }

  previous(): Promise<MoveResult> {
    return this.move('previous');
  }

  /** The document a move in `direction` would open, without fetching (prefetch, E16-T03). */
  peek(direction: CursorDirection): SearchHit | null {
    const rows = this.source.rows();
    if (this.left()) {
      const id = this.resume(direction);
      return id ? (rows.find((r) => r.documentId === id) ?? null) : null;
    }
    const i = this.index();
    if (i < 0) return null;
    return rows[direction === 'next' ? i + 1 : i - 1] ?? null;
  }

  /**
   * On the last loaded row, fetches the next cursor page in the background, so Next (and the prefetch of the
   * document after it) does not wait for the search at a page boundary.
   */
  loadAhead(): void {
    const i = this.index();
    if (i >= 0 && i === this.source.rows().length - 1 && this.source.hasMore('next')) {
      void this.source.fetchMore('next');
    }
  }

  showRelated(hit: SearchHit): void {
    this._related.set(hit.documentId === this._documentId() ? null : hit);
  }

  returnToCursor(): void {
    this._related.set(null);
  }

  private async move(direction: CursorDirection): Promise<MoveResult> {
    if (this._documentId() === null) return direction === 'next' ? 'end' : 'start';
    if (this.moving) return 'busy';
    this.moving = true;
    try {
      for (let attempt = 0; attempt < 3; attempt++) {
        const target = this.target(direction);
        if (target) {
          this.go(target);
          return 'moved';
        }
        if (!this.source.hasMore(direction)) break;
        const rows = this.source.rows();
        const added = await this.source.fetchMore(direction);
        // Nothing added and nothing refreshed: the list is busy (or failed and says so).
        if (!added && this.source.rows() === rows) return 'busy';
      }
      return direction === 'next' ? 'end' : 'start';
    } finally {
      this.moving = false;
    }
  }

  /** The id to move to among the loaded rows, or null when the move needs another page (or there is none). */
  private target(direction: CursorDirection): string | null {
    const rows = this.source.rows();
    if (this.left()) return this.resume(direction);
    const i = this.index() + (direction === 'next' ? 1 : -1);
    return rows[i]?.documentId ?? null;
  }

  /**
   * After the current document left the set: the first remembered neighbour still in it, else the row now at
   * the current document's old position (its successor moved up into it), else the nearest loaded row.
   */
  private resume(direction: CursorDirection): string | null {
    const rows = this.source.rows();
    if (rows.length === 0) return null;
    const ids = new Set(rows.map((r) => r.documentId));
    const neighbour = (direction === 'next' ? this.followers : this.predecessors).find((id) =>
      ids.has(id),
    );
    if (neighbour) return neighbour;
    if (this.lastPosition !== null) {
      for (let i = 0; i < rows.length; i++) {
        const p = this.source.positionOf(i);
        if (p === null) continue;
        if (direction === 'next' && p >= this.lastPosition) return rows[i].documentId;
        if (direction === 'previous' && p >= this.lastPosition)
          return rows[i - 1]?.documentId ?? null;
      }
      return direction === 'previous' ? rows[rows.length - 1].documentId : null;
    }
    const at = Math.min(Math.max(0, this.lastIndex), rows.length - 1);
    return direction === 'next' ? rows[at].documentId : (rows[at - 1]?.documentId ?? null);
  }

  private go(documentId: string): void {
    this._documentId.set(documentId);
    this._related.set(null);
    this.remember();
  }

  private remember(): void {
    const rows = this.source.rows();
    const i = this.index();
    if (i < 0) return;
    this.lastHit.set(rows[i]);
    this.lastIndex = i;
    this.lastPosition = this.source.positionOf(i);
    this.followers = rows.slice(i + 1, i + 1 + REMEMBERED).map((r) => r.documentId);
    this.predecessors = rows
      .slice(Math.max(0, i - REMEMBERED), i)
      .reverse()
      .map((r) => r.documentId);
  }
}
