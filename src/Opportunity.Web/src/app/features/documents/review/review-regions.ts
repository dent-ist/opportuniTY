import { ChangeDetectionStrategy, Component, Signal, computed, input } from '@angular/core';
import type { SearchHit } from '../../../core/api/generated/models';
import { Icon } from '../../../ui';

// Regions of Review mode (familiarity guide §3.2). Related Items (E16-T10) is still a placeholder with the typed
// inputs its ticket builds on. The viewer is viewer/document-viewer.ts (E16-T04) and the coding pane is
// coding/coding-pane.ts (E16-T05). Their regions, landmarks, sizes and keyboard commands belong to the review
// workspace (E16-T03).

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
