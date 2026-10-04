import { Injectable, inject } from '@angular/core';
import type { DocumentPageResource, DocumentResource } from '../../../core/api/generated/models';
import { ContentPurpose, DocumentContentApi, TextChunk } from './review-ports';
import {
  ModeAvailabilityMap,
  ViewerMode,
  ViewerModePreference,
  initialMode,
  modeAvailability,
} from './viewer/viewer-modes';

/** What the viewer shows first: the document's metadata and the first content of the mode it opens in. */
export interface LoadedDocument {
  readonly documentId: string;
  readonly metadata: DocumentResource;
  readonly availability: ModeAvailabilityMap;
  /** The mode chosen when it was loaded (the reviewer's last mode if the document has it, E16-T04). */
  readonly mode: ViewerMode;
  /** Chunk 0 of the extracted text, when it opens in Extracted Text. */
  readonly text: TextChunk | null;
  /** The page list and the first page's image, when it opens in Image. */
  readonly pages: readonly DocumentPageResource[] | null;
  readonly firstImage: { readonly pageNumber: number; readonly blob: Blob } | null;
  /** The gateway delivery shown first; the view (`Document.Viewed`) refers to it. */
  readonly retrievalId: string | null;
}

interface Entry {
  readonly promise: Promise<LoadedDocument>;
  value?: LoadedDocument;
}

/**
 * Loads what the viewer shows first (metadata, then the first text chunk or page image of the opening mode) and
 * prefetches the next document's, so Next shows it at once (ADR-018 §13.3: next document visible ≤ 500 ms with
 * prefetch). Every request goes through the protected-content gateway; prefetches carry `purpose=prefetch`, so
 * they are audited as prefetch and never as viewed (ADR-013). Displaying a prefetched document reuses that
 * delivery: the view is then recorded with its retrieval id. Only the documents around the cursor are kept.
 */
@Injectable()
export class DocumentLoader {
  private readonly api = inject(DocumentContentApi);
  private readonly preference = inject(ViewerModePreference);
  private readonly entries = new Map<string, Entry>();

  /** The document for display: the prefetched one if there is one, else display requests. */
  display(documentId: string): Promise<LoadedDocument> {
    const entry = this.entries.get(documentId);
    if (entry) return entry.promise;
    return this.fetch(documentId, 'display');
  }

  /** The document when it has already arrived (shown without a loading state). */
  loaded(documentId: string): LoadedDocument | undefined {
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

  private fetch(documentId: string, purpose: ContentPurpose): Promise<LoadedDocument> {
    const promise = this.load(documentId, purpose);
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

  private async load(documentId: string, purpose: ContentPurpose): Promise<LoadedDocument> {
    const metadata = await this.api.document(documentId, purpose);
    const availability = modeAvailability(metadata.value);
    const mode = initialMode(availability, this.preference.last());
    const loaded: LoadedDocument = {
      documentId,
      metadata: metadata.value,
      availability,
      mode,
      text: null,
      pages: null,
      firstImage: null,
      retrievalId: metadata.retrievalId,
    };
    if (mode === 'text') {
      const text = await this.api.textChunk(documentId, 0, purpose);
      return { ...loaded, text, retrievalId: text.retrievalId ?? loaded.retrievalId };
    }
    if (mode === 'image') {
      const pages = await this.api.pages(documentId, purpose);
      const first = pages.value.find((p) => p.hasImage);
      if (!first) return { ...loaded, pages: pages.value };
      const pageNumber = Number(first.pageNumber);
      const image = await this.api.pageImage(documentId, pageNumber, 'image', purpose);
      return {
        ...loaded,
        pages: pages.value,
        firstImage: { pageNumber, blob: image.value },
        retrievalId: image.retrievalId ?? loaded.retrievalId,
      };
    }
    return loaded;
  }
}
