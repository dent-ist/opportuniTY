import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { CodingApi, CodingIndexState, CodingValue, DocumentCoding } from '../review-ports';

interface PendingEntry {
  readonly version: string;
  readonly values: Readonly<Record<string, CodingValue>>;
  readonly state: CodingIndexState;
}

/** Waits between checks of a save's indexing state; the last one repeats. */
const POLL_DELAYS_MS = [500, 1_000, 2_000, 4_000, 8_000, 15_000];
/** Stop checking after this long; the save stays "indexing" until the document is read again. */
const POLL_LIMIT_MS = 10 * 60_000;

/**
 * The reviewer's own saves that search has not caught up with yet (read-your-own-writes, E16-T05, Q-10). The
 * coding pane shows "Saved · indexing" until the save is searchable; the document list marks the row and its coding
 * values come from here instead of the search page until then. Indexing state is checked with `GET …/coding` with
 * backoff, only for documents the reviewer saved. Provided by the Documents page, so it lives as long as the list.
 */
@Injectable()
export class PendingCoding {
  private readonly api = inject(CodingApi);
  private readonly entries = signal<ReadonlyMap<string, PendingEntry>>(new Map());
  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.timers.forEach((t) => clearTimeout(t)));
  }

  /** Records a save (or a read showing the document's coding still indexing) and follows it until searchable. */
  track(coding: DocumentCoding): void {
    const previous = this.entries().get(coding.documentId);
    if (coding.indexState === 'searchable' && !previous) return;
    this.set(coding.documentId, {
      version: coding.version,
      values: coding.values,
      state: coding.indexState,
    });
    if (coding.indexState === 'pending') this.poll(coding.documentId, 0, Date.now());
    else this.stop(coding.documentId);
  }

  /** The indexing state of the reviewer's last save of the document in this list, or null. */
  state(documentId: string): CodingIndexState | null {
    return this.entries().get(documentId)?.state ?? null;
  }

  /** True while the reviewer's own save of the document is not searchable yet. */
  isPending(documentId: string): boolean {
    const state = this.state(documentId);
    return state === 'pending' || state === 'failed';
  }

  /**
   * The value to show for a coding field of the document: the reviewer's own saved value while search has not
   * caught up with it, otherwise undefined (the search page's value is current).
   */
  overlay(documentId: string, queryName: string): CodingValue | undefined {
    const entry = this.entries().get(documentId);
    if (!entry || entry.state === 'searchable') return undefined;
    return entry.values[queryName] ?? null;
  }

  private poll(documentId: string, attempt: number, since: number): void {
    this.stop(documentId);
    if (Date.now() - since > POLL_LIMIT_MS) return;
    const delay = POLL_DELAYS_MS[Math.min(attempt, POLL_DELAYS_MS.length - 1)];
    this.timers.set(
      documentId,
      setTimeout(() => {
        this.timers.delete(documentId);
        this.api.get(documentId).then(
          (coding) => {
            const entry = this.entries().get(documentId);
            if (!entry) return;
            if (coding.indexState === 'pending') {
              this.poll(documentId, attempt + 1, since);
            } else {
              this.set(documentId, { ...entry, state: coding.indexState });
            }
          },
          () => this.poll(documentId, attempt + 1, since),
        );
      }, delay),
    );
  }

  private stop(documentId: string): void {
    clearTimeout(this.timers.get(documentId));
    this.timers.delete(documentId);
  }

  private set(documentId: string, entry: PendingEntry): void {
    const next = new Map(this.entries());
    next.set(documentId, entry);
    this.entries.set(next);
  }
}
