import { ChangeDetectionStrategy, Component, Signal, computed, input } from '@angular/core';
import type { SearchHit } from '../../../core/api/generated/models';
import { EmptyState, Icon, LoadingState } from '../../../ui';
import type { TextChunk } from './review-ports';

// The regions of Review mode (familiarity guide §3.2). The viewer and Related Items are placeholders with the typed
// inputs their tickets build on: the viewer modes (E16-T04, #130) and Related Items (E16-T10). The coding pane
// (E16-T05) is ./coding/coding-pane.ts. Their regions, landmarks, sizes and keyboard commands belong to the review
// workspace (E16-T03).

/** What the viewer shows: the displayed document and its first text chunk once loaded. */
export interface ViewerDocument {
  readonly hit: SearchHit;
  readonly state: 'loading' | 'ready' | 'unavailable';
  readonly text: TextChunk | null;
}

/**
 * The document viewer (E16-T04 replaces the body with the viewer modes). Until then it shows the start of the
 * extracted text, delivered through the protected-content gateway like every viewer request.
 */
@Component({
  selector: 'opp-review-viewer',
  imports: [EmptyState, Icon, LoadingState],
  template: `@let doc = document();
    <p class="pane__note">
      <opp-icon name="info" />
      <span
        >Viewer modes (Extracted Text, Image, Native, Production, Metadata) are coming soon. The
        start of the extracted text is shown.</span
      >
    </p>
    <div
      class="viewer__body"
      [attr.data-viewer-document]="doc.hit.documentId"
      [attr.data-state]="doc.state"
      [attr.aria-busy]="doc.state === 'loading' ? true : null"
    >
      @switch (doc.state) {
        @case ('loading') {
          <opp-loading-state label="Loading document…" />
        }
        @case ('unavailable') {
          <opp-empty-state
            title="This document cannot be shown"
            message="Its content is not available. Move to the next document or go back to the list."
          />
        }
        @default {
          @if (doc.text?.text) {
            <pre
              class="viewer__text"
              tabindex="0"
              [attr.aria-label]="'Extracted text of ' + doc.hit.controlNumber"
              >{{ doc.text!.text }}</pre>
            @if (doc.text!.partial) {
              <p class="pane__muted">More text follows in the full viewer.</p>
            }
          } @else {
            <opp-empty-state
              title="No extracted text"
              message="This document has no extracted text."
            />
          }
        }
      }
    </div>`,
  styleUrl: './review-regions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content' },
})
export class ReviewViewer {
  readonly document = input.required<ViewerDocument>();
}

/**
 * What Review mode needs from the coding pane (E16-T05): whether it holds unsaved edits, and saving or
 * discarding them. Save & Next, Save & Previous and the unsaved-changes prompt are built on this.
 */
export interface CodingEditor {
  readonly dirty: Signal<boolean>;
  /** Saves the edits; false when they could not be saved (validation, conflict) and the move must not happen. */
  save(): Promise<boolean>;
  discard(): void;
}

/** Related Items (E16-T10 lists Family, Duplicates and Email Thread members here). */
@Component({
  selector: 'opp-review-related',
  imports: [Icon],
  template: `<p class="pane__note">
      <opp-icon name="info" />
      <span
        >Family, Duplicates and Email Thread members of this document are listed here soon.</span
      >
    </p>
    <p class="related__family">{{ family() }}</p>`,
  styleUrl: './review-regions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content' },
})
export class ReviewRelated {
  readonly hit = input.required<SearchHit>();

  protected readonly family = computed(() => {
    const hit = this.hit();
    if (hit.parentDocumentId) return `${hit.controlNumber} is an attachment in a family.`;
    if (hit.familyId && hit.familyId !== hit.documentId)
      return `${hit.controlNumber} belongs to a family.`;
    return `${hit.controlNumber} is a parent or stand-alone document.`;
  });
}
