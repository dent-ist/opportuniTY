import { Injectable, signal } from '@angular/core';
import { ApiError } from '../../../core/api/problem-details';

/**
 * A document the API now answers like a missing one (404): hidden by a restriction class or an ethical wall, or
 * deleted. The API never says which (Q-11, Q-13), and neither does the UI.
 */
export class DocumentUnavailableError extends Error {
  constructor(readonly documentId: string) {
    super('The document is not available.');
    this.name = 'DocumentUnavailableError';
  }
}

/** True for the 404 of a document route: the document is hidden from the caller or does not exist. */
export function isDocumentNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/**
 * Documents that stopped being available during this visit to Documents (E16-T08, familiarity guide §3.5): a list
 * row loaded before access was revoked, a document that became hidden while open (another user's change, a new
 * wall), or one the reviewer's own confirmed save hid from them.
 *
 * Once a document is here, nothing more is requested for it (no metadata, text, pages, images, hits, coding or
 * prefetch): its list row becomes "No longer available" without metadata, and opening it shows the standard
 * no-access state. The set lives as long as the Documents page; a new search never returns these documents anyway,
 * because the API drops hits the caller may not see (Q-12).
 */
@Injectable()
export class DocumentAccess {
  private readonly ids = signal<ReadonlySet<string>>(new Set());

  /** The documents no longer available, for templates and effects. */
  readonly unavailable = this.ids.asReadonly();

  isUnavailable(documentId: string | null | undefined): boolean {
    return !!documentId && this.ids().has(documentId);
  }

  /** Records that `documentId` answered like a missing document. */
  markUnavailable(documentId: string): void {
    if (this.ids().has(documentId)) return;
    this.ids.update((ids) => new Set([...ids, documentId]));
  }
}
