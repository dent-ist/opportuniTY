import { Injectable, inject } from '@angular/core';
import { DocumentContentApi, TextChunk } from './review-ports';

interface Entry {
  readonly promise: Promise<TextChunk>;
  value?: TextChunk;
}

/**
 * Loads what the viewer shows first (the first text chunk) and prefetches the next document's, so Next shows it
 * at once (ADR-018 §13.3: next document visible ≤ 500 ms with prefetch). Every request goes through the
 * protected-content gateway; prefetches carry `purpose=prefetch`, so they are audited as prefetch and never as
 * viewed (ADR-013). Displaying a prefetched document reuses that delivery: the view is then recorded with its
 * retrieval id. Only the documents around the cursor are kept.
 */
@Injectable()
export class DocumentLoader {
  private readonly api = inject(DocumentContentApi);
  private readonly entries = new Map<string, Entry>();

  /** The document's first chunk for display: the prefetched one if there is one, else a display request. */
  display(documentId: string): Promise<TextChunk> {
    const entry = this.entries.get(documentId);
    if (entry) return entry.promise;
    return this.fetch(documentId, 'display');
  }

  /** The chunk when it has already arrived (shown without a loading state). */
  loaded(documentId: string): TextChunk | undefined {
    return this.entries.get(documentId)?.value;
  }

  /** Starts loading `documentId` in the background, unless it is loaded or on its way. */
  prefetch(documentId: string): void {
    if (this.entries.has(documentId)) return;
    this.fetch(documentId, 'prefetch').catch(() => undefined); // retried for display
  }

  /** Forgets every document except `keep` (the current one and its neighbours). */
  retain(keep: readonly string[]): void {
    const wanted = new Set(keep);
    for (const id of [...this.entries.keys()]) if (!wanted.has(id)) this.entries.delete(id);
  }

  private fetch(documentId: string, purpose: 'display' | 'prefetch'): Promise<TextChunk> {
    const promise = this.api.firstText(documentId, purpose);
    const entry: Entry = { promise };
    this.entries.set(documentId, entry);
    promise.then(
      (value) => (entry.value = value),
      () => {
        // A failed load is not cached: the next display asks again.
        if (this.entries.get(documentId) === entry) this.entries.delete(documentId);
      },
    );
    return promise;
  }
}
