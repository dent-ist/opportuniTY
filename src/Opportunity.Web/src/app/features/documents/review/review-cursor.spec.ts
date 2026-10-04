import { signal } from '@angular/core';
import type { SearchHit } from '../../../core/api/generated/models';
import { hit } from '../grid/grid-fixtures.testing';
import { CursorDirection, CursorSource, ReviewCursor } from './review-cursor';

/**
 * A result of documents 1…total in pages of `pageSize`, with pages `from`…`to` loaded; positions are known.
 * `refresh` replaces the loaded rows the way the grid does when the search runs again (Q-33).
 */
class FakeSource implements CursorSource {
  readonly rows = signal<readonly SearchHit[]>([]);
  readonly countText = signal('');
  fetches: CursorDirection[] = [];
  private docs: number[];
  private from: number;
  private to: number;

  constructor(
    total: number,
    private readonly pageSize: number,
    loaded: [number, number] = [1, 1],
  ) {
    this.docs = Array.from({ length: total }, (_, i) => i + 1);
    [this.from, this.to] = loaded;
    this.sync();
  }

  positionOf(index: number): number | null {
    return index >= 0 && index < this.rows().length
      ? (this.from - 1) * this.pageSize + index + 1
      : null;
  }

  hasMore(direction: CursorDirection): boolean {
    return direction === 'next' ? this.to * this.pageSize < this.docs.length : this.from > 1;
  }

  async fetchMore(direction: CursorDirection): Promise<boolean> {
    this.fetches.push(direction);
    if (!this.hasMore(direction)) return false;
    if (direction === 'next') this.to++;
    else this.from--;
    this.sync();
    return true;
  }

  /** The documents left the set and the search ran again with page `page` loaded. */
  refresh(remove: number[], page: number): void {
    this.docs = this.docs.filter((n) => !remove.includes(n));
    this.from = this.to = page;
    this.sync();
  }

  private sync(): void {
    const start = (this.from - 1) * this.pageSize;
    const end = this.to * this.pageSize;
    this.rows.set(this.docs.slice(start, end).map((n) => hit(n)));
    this.countText.set(String(this.docs.length));
  }
}

describe('ReviewCursor (E16-T03)', () => {
  it('walks the list in order with its position, and never wraps at either end', async () => {
    const source = new FakeSource(3, 10);
    const cursor = new ReviewCursor(source);
    cursor.open('doc-2');
    expect(cursor.position()).toBe(2);
    expect(cursor.total()).toBe('3');
    expect(await cursor.next()).toBe('moved');
    expect(cursor.documentId()).toBe('doc-3');
    expect(cursor.hasNext()).toBe(false);
    expect(await cursor.next()).toBe('end');
    expect(cursor.documentId()).toBe('doc-3');
    expect(await cursor.previous()).toBe('moved');
    expect(await cursor.previous()).toBe('moved');
    expect(await cursor.previous()).toBe('start');
    expect(cursor.documentId()).toBe('doc-1');
  });

  it('fetches the next and previous cursor pages when the loaded rows run out', async () => {
    const source = new FakeSource(25, 10, [2, 2]);
    const cursor = new ReviewCursor(source);
    cursor.open('doc-20');
    expect(cursor.peek('next')).toBeNull();
    expect(await cursor.next()).toBe('moved');
    expect(source.fetches).toEqual(['next']);
    expect(cursor.documentId()).toBe('doc-21');
    expect(cursor.position()).toBe(21);

    cursor.open('doc-11');
    expect(await cursor.previous()).toBe('moved');
    expect(source.fetches).toEqual(['next', 'previous']);
    expect(cursor.documentId()).toBe('doc-10');
    // Rows were added above: the cursor holds the document, not an index.
    expect(cursor.index()).toBe(9);
    expect(cursor.position()).toBe(10);
  });

  it('keeps its document when the results refresh, and continues from the next one after it left the set', async () => {
    const source = new FakeSource(30, 10, [1, 1]);
    const cursor = new ReviewCursor(source);
    cursor.open('doc-4');
    source.refresh([], 1);
    expect(cursor.left()).toBe(false);
    expect(cursor.position()).toBe(4);

    // doc-4 is recoded out of the set: its neighbours are remembered.
    source.refresh([4], 1);
    expect(cursor.left()).toBe(true);
    expect(cursor.hit()?.controlNumber).toBe('ACM0000004');
    expect(cursor.peek('next')?.documentId).toBe('doc-5');
    expect(await cursor.next()).toBe('moved');
    expect(cursor.documentId()).toBe('doc-5');
    expect(cursor.left()).toBe(false);

    source.refresh([5], 1);
    expect(await cursor.previous()).toBe('moved');
    expect(cursor.documentId()).toBe('doc-3');
  });

  it('continues by position when the document at the end of a page left the set', async () => {
    const source = new FakeSource(30, 10, [1, 1]);
    const cursor = new ReviewCursor(source);
    cursor.open('doc-10'); // the last loaded row: no neighbour after it is known
    source.refresh([10], 1); // doc-11 moved up into position 10
    expect(cursor.left()).toBe(true);
    expect(await cursor.next()).toBe('moved');
    expect(cursor.documentId()).toBe('doc-11');
  });

  it('shows a related item without moving the cursor, and Previous/Next walk the original set', async () => {
    const source = new FakeSource(5, 10);
    const cursor = new ReviewCursor(source);
    cursor.open('doc-2');
    cursor.showRelated(hit(99));
    expect(cursor.displayed()?.documentId).toBe('doc-99');
    expect(cursor.documentId()).toBe('doc-2');
    cursor.returnToCursor();
    expect(cursor.displayed()?.documentId).toBe('doc-2');
    cursor.showRelated(hit(99));
    expect(await cursor.next()).toBe('moved');
    expect(cursor.documentId()).toBe('doc-3');
    expect(cursor.related()).toBeNull();
  });
});
